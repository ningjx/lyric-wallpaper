using W = System.Windows;

namespace LyricWallpaper.Presentation;

// Analytical spring integration and constants adapted from liquid-glass-webgl (Apache-2.0).
internal struct GlassSpring(double position)
{
    public double Position = position;
    public double Velocity;

    public bool Step(double target, double dt, double stiffness, double damping)
    {
        if (!W.SystemParameters.ClientAreaAnimation ||
            (Math.Abs(Position - target) < .003 && Math.Abs(Velocity) < .003))
        {
            Snap(target);
            return false;
        }

        double x = Position - target, v = Velocity;
        double rate = Math.Sqrt(stiffness), decayRate = rate * damping, decay = Math.Exp(-decayRate * dt);
        if (damping == 1)
        {
            double amplitude = v + rate * x;
            Position = target + (x + amplitude * dt) * decay;
            Velocity = (v - rate * amplitude * dt) * decay;
        }
        else
        {
            double frequency = rate * Math.Sqrt(1 - damping * damping);
            double b = (v + decayRate * x) / frequency;
            double cos = Math.Cos(frequency * dt), sin = Math.Sin(frequency * dt);
            double offset = decay * (x * cos + b * sin);
            Position = target + offset;
            Velocity = -decayRate * offset + decay * frequency * (b * cos - x * sin);
        }
        return true;
    }

    public void Snap(double target) { Position = target; Velocity = 0; }
}

internal sealed class GlassGesture(double heldScale)
{
    private GlassSpring position = new(0), press = new(0), x = new(1), y = new(1), squash = new(0);
    private double target;
    private bool initialized, held, releasePending, driven;
    public double Position => position.Position;
    public double Target => target;
    public float Press => (float)Math.Clamp(press.Position, 0, 1);
    public float ScaleX(double divisor) => (float)(x.Position / (1 - Math.Clamp(squash.Position / divisor * .75, -.2, .2)));
    public float ScaleY(double divisor) => (float)(y.Position / (1 - Math.Clamp(-squash.Position / divisor * .25, -.2, .2)));

    public void Set(double value)
    {
        target = value;
        if (initialized) return;
        position.Snap(value);
        initialized = true;
    }

    public void Begin()
    {
        held = true;
        releasePending = driven = false;
    }

    public void Move(double value)
    {
        Set(value);
        driven = true;
    }

    public void End(double value)
    {
        Set(value);
        releasePending = held;
        held = false;
    }

    public bool Advance(double dt)
    {
        bool active = position.Step(target, dt, 1000, 1);
        if (releasePending && Math.Abs(position.Position - target) < .02) releasePending = false;
        if (!active && !held) driven = false;
        bool lit = held || releasePending;
        active |= press.Step(lit ? 1 : 0, dt, 1000, 1);
        double size = lit && W.SystemParameters.ClientAreaAnimation ? heldScale : 1;
        active |= x.Step(size, dt, 250, .6);
        active |= y.Step(size, dt, 250, .7);
        active |= squash.Step(driven ? position.Velocity : 0, dt, 300, .5);
        return active;
    }
}
