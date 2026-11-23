using System.Numerics;

namespace LyricWallpaper.Widgets;

internal readonly record struct WidgetPosition(float X, float Y)
{
    public static WidgetPosition Center => new(.5f, .5f);

    public Vector2 InPixels(Vector2 size, Vector2 half)
    {
        half = Vector2.Min(half, size / 2);
        return Vector2.Clamp(new Vector2(X, Y) * size, half, size - half);
    }
}

internal sealed class WidgetPressFeedback
{
    private const double MinimumPressDuration = .1;
    private double velocity;
    private double releaseAfter;
    private float target;

    public Guid? WidgetId { get; private set; }
    public float Press { get; private set; }
    public bool IsAnimating => Press != target || velocity != 0;

    public void Begin(Guid widget, double now)
    {
        if (WidgetId != widget)
        {
            Press = target = 0;
            velocity = 0;
            WidgetId = widget;
        }
        releaseAfter = now + MinimumPressDuration;
    }

    public double NextChange(double now) => now < releaseAfter ? releaseAfter : double.PositiveInfinity;

    public void Animate(double elapsed, double now, Guid? heldWidget, bool reduceMotion)
    {
        bool pressed = WidgetId is { } widget && (heldWidget == widget || now < releaseAfter);
        target = pressed ? 1 : 0;
        if (reduceMotion)
        {
            Press = target;
            velocity = 0;
            return;
        }
        if (!IsAnimating) return;

        // Liquid Glass uses an underdamped spring with stiffness 300 and damping ratio 0.5.
        double decay = Math.Sqrt(300) * .5;
        const double frequency = 15;
        double step = Math.Min(elapsed, .05);
        double angle = frequency * step;
        double envelope = Math.Exp(-decay * step);
        double delta = Press - target;
        double sineTerm = (velocity + decay * delta) / frequency;
        double displacement = delta * Math.Cos(angle) + sineTerm * Math.Sin(angle);
        velocity = envelope * (frequency * (sineTerm * Math.Cos(angle) - delta * Math.Sin(angle)) - decay * displacement);
        Press = (float)(target + envelope * displacement);
        if (Math.Abs(Press - target) < .002 && Math.Abs(velocity) < .04)
        {
            Press = target;
            velocity = 0;
        }
    }
}
