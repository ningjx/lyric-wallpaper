using System.Numerics;
using W = System.Windows;
using Ui = System.Windows.Controls;
using Input = System.Windows.Input;
using Primitives = System.Windows.Controls.Primitives;
using Fluent = Wpf.Ui.Controls;

namespace LyricWallpaper.Presentation;

internal static class GlassInput
{
    public static readonly Vector4 Accent = new(0, .478f, 1, 1);
    public static readonly Vector4 Well = new(.47f, .49f, .50f, .27f);
    public static void Invalidate(W.DependencyObject element)
    {
        if (W.Window.GetWindow(element) is SettingsWindow window) window.InvalidateGlass();
    }
    public static W.Rect Centered(double x, double y, double w, double h) => new(x - w / 2, y - h / 2, w, h);
}

internal sealed class NativeGlassButton : Fluent.Button, INativeGlassControl
{
    private GlassSpring press = new(0);

    protected override void OnPropertyChanged(W.DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == IsPressedProperty || e.Property == IsEnabledProperty) GlassInput.Invalidate(this);
    }

    public bool Compose(GlassFrame frame, W.Rect bounds, W.Rect clip, double elapsed)
    {
        bool active = press.Step(IsPressed ? 1 : 0, elapsed, 300, .5);
        float progress = (float)Math.Clamp(press.Position, 0, 1);
        float scale = 1 + .055f * progress;
        var surface = new Vector4(1, 1, 1, IsEnabled ? .30f : .16f);
        frame.Shape(frame.Lenses, bounds, clip, surface, (float)bounds.Height / 2, new(12, -24, 2),
            new(scale, scale, .5f, 0), new(0, 0, 24, .10f));
        return active;
    }
}

internal sealed class NativeGlassSwitch : Primitives.ToggleButton, INativeGlassControl
{
    private readonly GlassGesture gesture = new(1.5);
    private W.Point down;
    private bool moving, dragging, ending;
    private double start;

    private double CheckedFraction => IsChecked == true ? 1 : 0;

    protected override void OnChecked(W.RoutedEventArgs e) { base.OnChecked(e); Sync(); }
    protected override void OnUnchecked(W.RoutedEventArgs e) { base.OnUnchecked(e); Sync(); }
    public NativeGlassSwitch() => IsEnabledChanged += (_, _) => GlassInput.Invalidate(this);

    private void Sync()
    {
        if (!dragging) gesture.Set(CheckedFraction);
        GlassInput.Invalidate(this);
    }

    protected override void OnMouseLeftButtonDown(Input.MouseButtonEventArgs e)
    {
        Focus();
        if (!CaptureMouse()) return;
        dragging = true;
        moving = false;
        down = e.GetPosition(this);
        start = gesture.Position;
        gesture.Begin();
        GlassInput.Invalidate(this);
        e.Handled = true;
    }

    protected override void OnMouseMove(Input.MouseEventArgs e)
    {
        if (!dragging) return;
        double delta = e.GetPosition(this).X - down.X;
        if (!moving && Math.Abs(delta) < W.SystemParameters.MinimumHorizontalDragDistance) return;
        moving = true;
        gesture.Move(Math.Clamp(start + delta / 20, 0, 1));
        GlassInput.Invalidate(this);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(Input.MouseButtonEventArgs e)
    {
        if (!dragging) return;
        bool inside = new W.Rect(RenderSize).Contains(e.GetPosition(this));
        if (moving) IsChecked = gesture.Target >= .5;
        else if (inside) OnClick();
        End();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(Input.MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (dragging && !ending) End();
    }

    private void End()
    {
        dragging = false;
        ending = true;
        if (IsMouseCaptured) ReleaseMouseCapture();
        ending = false;
        gesture.End(CheckedFraction);
        GlassInput.Invalidate(this);
    }

    public bool Compose(GlassFrame frame, W.Rect bounds, W.Rect clip, double elapsed)
    {
        if (!dragging) gesture.Set(CheckedFraction);
        bool active = gesture.Advance(elapsed);
        float position = (float)Math.Clamp(gesture.Position, 0, 1);
        float p = gesture.Press;
        double cy = bounds.Top + bounds.Height / 2, x = bounds.Right - 64;
        float opacity = IsEnabled ? 1 : .45f;
        var color = Vector4.Lerp(GlassInput.Well, GlassInput.Accent, position);
        color.W *= opacity;
        frame.Shape(frame.Paint, new(x, cy - 14, 64, 28), clip, color, 14);
        // LiquidToggle uses a 40 × 24dp knob over a 64 × 28dp track, with 20dp of travel.
        double knob = x + 22 + 20 * position;
        float trackScale = (2f + .25f * p) / 3;
        frame.Shape(frame.Lenses, GlassInput.Centered(knob, cy, 40, 24), clip,
            new(1, 1, 1, (1 - p) * opacity), 12, new(5 * p, -10 * p, 8 * (1 - p)),
            new(gesture.ScaleX(50), gesture.ScaleY(50), p * .17f, 0), new(4 * p, .15f * p, 4, .05f),
            new(0, 0, 1, 0), new((float)(knob + (x + 32 - knob) * trackScale), (float)cy, 32 * trackScale, 10.5f * p), color);
        return active;
    }
}

internal sealed class NativeGlassSlider : Ui.Slider, INativeGlassControl
{
    private readonly GlassGesture gesture = new(1.5);
    private bool dragging;
    private double offset;
    private double Fraction => Maximum > Minimum ? (Value - Minimum) / (Maximum - Minimum) : 0;
    private double Travel => Math.Max(1, ActualWidth - 40);

    protected override void OnValueChanged(double oldValue, double newValue)
    {
        base.OnValueChanged(oldValue, newValue);
        if (!dragging) gesture.Set(Fraction);
        GlassInput.Invalidate(this);
    }

    protected override void OnMouseLeftButtonDown(Input.MouseButtonEventArgs e)
    {
        Focus();
        if (!CaptureMouse()) return;
        dragging = true;
        double x = e.GetPosition(this).X;
        double center = 20 + Travel * gesture.Position;
        offset = Math.Abs(x - center) <= 20 ? x - center : 0;
        gesture.Begin();
        SetPointer(x);
        e.Handled = true;
    }

    protected override void OnMouseMove(Input.MouseEventArgs e)
    {
        if (!dragging) return;
        SetPointer(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(Input.MouseButtonEventArgs e)
    {
        if (!dragging) return;
        SetPointer(e.GetPosition(this).X);
        End();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(Input.MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (dragging) End();
    }

    private void SetPointer(double x)
    {
        double fraction = Math.Clamp((x - offset - 20) / Travel, 0, 1);
        double value = Minimum + (Maximum - Minimum) * fraction;
        if (IsSnapToTickEnabled && TickFrequency > 0)
            value = fraction == 1 ? Maximum : Math.Clamp(Minimum + Math.Round((value - Minimum) / TickFrequency) * TickFrequency, Minimum, Maximum);
        Value = value;
        gesture.Move(fraction);
        GlassInput.Invalidate(this);
    }

    private void End()
    {
        dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        gesture.End(Fraction);
        GlassInput.Invalidate(this);
    }

    public bool Compose(GlassFrame frame, W.Rect bounds, W.Rect clip, double elapsed)
    {
        if (!dragging) gesture.Set(Fraction);
        bool active = gesture.Advance(elapsed);
        double y = bounds.Top + bounds.Height / 2;
        double x = bounds.Left + 20 + Travel * Math.Clamp(gesture.Position, 0, 1);
        float p = gesture.Press;
        frame.Shape(frame.Paint, new(bounds.Left + 10, y - 3, Math.Max(1, bounds.Width - 20), 6), clip, GlassInput.Well, 3);
        frame.Shape(frame.Paint, new(bounds.Left + 10, y - 3, x - bounds.Left - 10, 6), clip, GlassInput.Accent, 3);
        frame.Shape(frame.Lenses, GlassInput.Centered(x, y, 40, 24), clip, new(1, 1, 1, 1 - p), 12,
            new(10 * p, -14 * p, 8 * (1 - p)), new(gesture.ScaleX(10), gesture.ScaleY(10), .18f * p, 0),
            new(4 * p, .3f * p, 4, .05f), new((2 + p) / 3, p, 2, 0));
        return active;
    }
}

internal sealed class NativeGlassSelector(bool navigation) : Ui.ListBox, INativeGlassControl
{
    public const double Inset = 4;
    private readonly GlassGesture gesture = new(navigation ? 1.10 : 1.2);
    private bool dragging;
    private bool moved;
    private double startPosition, startFraction;
    private double TrackLength => Math.Max(1, (navigation ? ActualHeight : ActualWidth) - 2 * Inset);
    private double SelectedFraction => Items.Count > 1 ? (double)Math.Max(0, SelectedIndex) / (Items.Count - 1) : 0;
    private double PositionOnTrack(W.Point point) => navigation ? point.Y : point.X;

    protected override void OnSelectionChanged(Ui.SelectionChangedEventArgs e)
    {
        base.OnSelectionChanged(e);
        if (!dragging) gesture.Set(SelectedFraction);
        GlassInput.Invalidate(this);
    }

    protected override void OnPreviewKeyDown(Input.KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        int direction = (navigation, e.Key) switch
        {
            (true, Input.Key.Up) or (false, Input.Key.Left) => -1,
            (true, Input.Key.Down) or (false, Input.Key.Right) => 1,
            _ => 0
        };
        if (e.Handled || direction == 0 || e.KeyboardDevice.Modifiers != Input.ModifierKeys.None || Items.Count == 0) return;
        Select(Math.Clamp(SelectedIndex + direction, 0, Items.Count - 1));
        e.Handled = true;
    }

    protected override void OnPreviewMouseLeftButtonDown(Input.MouseButtonEventArgs e)
    {
        if (Items.Count == 0 || !CaptureMouse()) return;
        dragging = true;
        moved = false;
        startPosition = PositionOnTrack(e.GetPosition(this));
        startFraction = gesture.Position;
        gesture.Begin();
        GlassInput.Invalidate(this);
        e.Handled = true;
    }

    protected override void OnPreviewMouseMove(Input.MouseEventArgs e)
    {
        if (!dragging || Items.Count < 2) return;
        double delta = PositionOnTrack(e.GetPosition(this)) - startPosition;
        double threshold = navigation ? W.SystemParameters.MinimumVerticalDragDistance : W.SystemParameters.MinimumHorizontalDragDistance;
        if (!moved && Math.Abs(delta) < threshold) return;
        moved = true;
        double span = Math.Max(1, TrackLength * (Items.Count - 1) / Items.Count);
        gesture.Move(Math.Clamp(startFraction + delta / span, 0, 1));
        GlassInput.Invalidate(this);
        e.Handled = true;
    }

    protected override void OnPreviewMouseLeftButtonUp(Input.MouseButtonEventArgs e)
    {
        if (!dragging) return;
        if (Items.Count != 0)
        {
            var point = e.GetPosition(this);
            if (moved)
                Select((int)Math.Round(gesture.Target * (Items.Count - 1), MidpointRounding.AwayFromZero));
            else if (new W.Rect(RenderSize).Contains(point))
                Select(Math.Clamp((int)((PositionOnTrack(point) - Inset) / TrackLength * Items.Count), 0, Items.Count - 1));
        }
        End();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(Input.MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (dragging) End();
    }

    private void Select(int index)
    {
        SelectedIndex = index;
        if (ItemContainerGenerator.ContainerFromIndex(index) is Ui.ListBoxItem cell) cell.Focus();
    }

    private void End()
    {
        dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        gesture.End(SelectedFraction);
        GlassInput.Invalidate(this);
    }

    public bool Compose(GlassFrame frame, W.Rect bounds, W.Rect clip, double elapsed)
    {
        if (Items.Count == 0 || bounds.Width <= 2 * Inset || bounds.Height <= 2 * Inset) return false;
        if (!dragging) gesture.Set(SelectedFraction);
        bool active = gesture.Advance(elapsed);
        float p = gesture.Press;
        float radius = navigation ? 25 : (float)bounds.Height / 2;
        float expansion = (navigation ? 8 : 16) * p;
        float scaleX = 1 + expansion / (float)bounds.Width;
        float scaleY = navigation ? 1 + expansion / (float)bounds.Height : scaleX;
        var track = bounds;
        track.Inflate(-Inset, -Inset);
        double cellLength = (navigation ? track.Height : track.Width) / Items.Count;
        float opticsScale = Math.Min(1, (float)(navigation ? cellLength + 2 * Inset : bounds.Height) / 64);
        frame.Shape(frame.Docks, bounds, clip, new(1, 1, 1, .10f), radius,
            new(24 * opticsScale, -24 * opticsScale, 8 * opticsScale), new(scaleX, scaleY, .32f, 0),
            new(0, 0, 4, .09f));
        for (int i = 0; i < Items.Count; i++)
        {
            string label = Items[i] is Ui.ListBoxItem item ? item.Content?.ToString() ?? "" : Items[i]?.ToString() ?? "";
            string? icon = navigation && Items[i] is Ui.ListBoxItem { Tag: string glyph } ? glyph : null;
            frame.Labels.Add(new(label, CellBounds(track, cellLength, i), clip, 12.5, icon));
        }
        double selected = Math.Clamp(gesture.Position, 0, 1) * (Items.Count - 1);
        var indicator = CellBounds(track, cellLength, selected);
        float lensScaleX = gesture.ScaleX(10), lensScaleY = gesture.ScaleY(10);
        if (navigation) (lensScaleX, lensScaleY) = (lensScaleY, lensScaleX);
        frame.Shape(frame.Lenses, indicator, clip, new(0, 0, 0, .10f - .07f * p), (float)indicator.Height / 2,
            new(10 * p * opticsScale, -14 * p * opticsScale, 0), new(lensScaleX, lensScaleY, .50f * p, 1),
            new(8 * p * opticsScale, .30f * p, 4, .12f * p));
        return active;
    }

    private W.Rect CellBounds(W.Rect track, double length, double index) => navigation
        ? new(track.X, track.Y + length * index, track.Width, length)
        : new(track.X + length * index, track.Y, length, track.Height);
}
