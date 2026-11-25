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
        if (W.Window.GetWindow(element) is INativeGlassHost host) host.InvalidateGlass();
    }
    public static W.Rect Centered(double x, double y, double w, double h) => new(x - w / 2, y - h / 2, w, h);
}

internal sealed class NativeGlassButton : Fluent.Button, INativeGlassControl
{
    private static readonly Vector4 AccentSurface = new(0, 94 / 255f, 184 / 255f, 1);
    public static readonly W.DependencyProperty IsAccentProperty =
        W.DependencyProperty.Register(nameof(IsAccent), typeof(bool), typeof(NativeGlassButton), new W.PropertyMetadata(false));
    private GlassSpring press = new(0);
    private Vector4? swatchColor;

    public bool IsAccent
    {
        get => (bool)GetValue(IsAccentProperty);
        set => SetValue(IsAccentProperty, value);
    }

    public Vector4? SwatchColor
    {
        get => swatchColor;
        set
        {
            if (swatchColor == value) return;
            swatchColor = value;
            GlassInput.Invalidate(this);
        }
    }

    protected override void OnPropertyChanged(W.DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == IsPressedProperty || e.Property == IsEnabledProperty || e.Property == IsAccentProperty)
            GlassInput.Invalidate(this);
    }

    public bool Compose(GlassFrame frame, W.Rect bounds, W.Rect clip, double elapsed)
    {
        bool active = press.Step(IsPressed ? 1 : 0, elapsed, 300, .5);
        float progress = (float)Math.Clamp(press.Position, 0, 1);
        float radius = (float)bounds.Height / 2;
        float size = (float)bounds.Height / 48;
        var surface = swatchColor ?? (IsAccent && IsEnabled ? AccentSurface : new(1, 1, 1, IsEnabled ? .30f : .16f));
        Vector4 sampling = swatchColor.HasValue ? new(0, 0, 3, 0) : default;
        frame.Shape(frame.Surfaces, bounds, clip, surface, radius, style: new(1, 1, IsEnabled ? .82f : .45f, 0), sampling: sampling);
        if (progress > .001f)
        {
            // LiquidButton grows by 4dp on a 48dp button. The front lens samples the face underneath.
            float scale = 1 + (4f / 48) * progress;
            frame.Shape(frame.Lenses, bounds, clip, Vector4.Zero, radius,
                new(12 * size, -24 * size * progress, 2 * size), new(scale, scale, .82f * progress, 0));
        }
        return active;
    }
}

internal sealed class NativeGlassSwitch : Primitives.ToggleButton, INativeGlassControl
{
    private const float TrackWidth = 48, TrackHeight = 20, ThumbInset = 1;
    private const float ThumbWidth = 26, ThumbHeight = TrackHeight - 2 * ThumbInset;
    private const float ThumbTravel = TrackWidth - ThumbWidth - 2 * ThumbInset;
    private readonly GlassGesture gesture = new(1.18);
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
        gesture.Move(Math.Clamp(start + delta / ThumbTravel, 0, 1));
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
        double cy = bounds.Top + bounds.Height / 2, x = bounds.Right - TrackWidth;
        float opacity = IsEnabled ? 1 : .45f;
        var color = Vector4.Lerp(GlassInput.Well, GlassInput.Accent, position);
        color.W *= opacity;
        frame.Shape(frame.Paint, new(x, cy - TrackHeight / 2, TrackWidth, TrackHeight), clip, color, TrackHeight / 2);
        double knob = x + ThumbInset + ThumbWidth / 2 + ThumbTravel * position;
        float trackScale = (2f + .25f * p) / 3;
        float sampleCenter = (float)(knob + (x + TrackWidth / 2 - knob) * trackScale);
        frame.Shape(frame.Lenses, GlassInput.Centered(knob, cy, ThumbWidth, ThumbHeight), clip,
            new(1, 1, 1, (1 - p) * opacity), ThumbHeight / 2, new(3 * p, -6 * p, 6 * (1 - p)),
            new(gesture.ScaleX(50), gesture.ScaleY(50), p * .17f, 0), new(0, 0, 1, 0),
            new(sampleCenter, (float)cy, TrackWidth / 2 * trackScale, TrackHeight * .375f * p), color);
        return active;
    }
}

internal sealed class NativeGlassSlider : Ui.Slider, INativeGlassControl
{
    private const float ThumbWidth = 30, ThumbHeight = 18;
    private const double TrackInset = 20;
    private readonly GlassGesture gesture = new(1.3);
    private bool dragging;
    private double offset;
    private double Fraction => Maximum > Minimum ? (Value - Minimum) / (Maximum - Minimum) : 0;
    private double Travel => Math.Max(1, ActualWidth - 2 * TrackInset);

    protected override void OnValueChanged(double oldValue, double newValue)
    {
        base.OnValueChanged(oldValue, newValue);
        if (!dragging) gesture.Set(Fraction);
        GlassInput.Invalidate(this);
    }

    protected override void OnPreviewMouseLeftButtonDown(Input.MouseButtonEventArgs e)
    {
        // The base Slider can handle the bubbling press when taking keyboard focus.
        Focus();
        if (!CaptureMouse()) return;
        dragging = true;
        double x = e.GetPosition(this).X;
        double center = TrackInset + Travel * Math.Clamp(gesture.Position, 0, 1);
        offset = Math.Abs(x - center) <= ThumbWidth / 2 ? x - center : 0;
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
        double fraction = Math.Clamp((x - offset - TrackInset) / Travel, 0, 1);
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
        double x = bounds.Left + TrackInset + Travel * Math.Clamp(gesture.Position, 0, 1);
        float p = gesture.Press;
        frame.Shape(frame.Paint, new(bounds.Left + 10, y - 3, Math.Max(1, bounds.Width - 20), 6), clip, GlassInput.Well, 3);
        frame.Shape(frame.Paint, new(bounds.Left + 10, y - 3, x - bounds.Left - 10, 6), clip, GlassInput.Accent, 3);
        frame.Shape(frame.Lenses, GlassInput.Centered(x, y, ThumbWidth, ThumbHeight), clip, new(1, 1, 1, 1 - p), ThumbHeight / 2,
            new(8 * p, -11 * p, 6 * (1 - p)), new(gesture.ScaleX(10), gesture.ScaleY(10), .18f * p, 0),
            new((2 + p) / 3, p, 2, 0));
        return active;
    }
}

internal sealed class NativeGlassSelector(bool navigation) : Ui.ListBox, INativeGlassControl
{
    public const double Inset = 4;
    // LiquidBottomTabs expands its 56dp indicator to 78dp while pressed.
    private readonly GlassGesture gesture = new(navigation ? 1.10 : 78d / 56);
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
        var track = bounds;
        track.Inflate(-Inset, navigation ? -Inset : -bounds.Height / 16);
        double cellLength = (navigation ? track.Height : track.Width) / Items.Count;
        float opticsScale = Math.Min(1, (float)(navigation ? cellLength + 2 * Inset : bounds.Height) / 64);
        if (!navigation)
        {
            frame.Shape(frame.Surfaces, bounds, clip, new(1, 1, 1, .35f), (float)bounds.Height / 2,
                new(24 * opticsScale, -24 * opticsScale, 8 * opticsScale), new(1, 1, .80f, 0));
        }
        for (int i = 0; i < Items.Count; i++)
        {
            string label = Items[i] is Ui.ListBoxItem item ? item.Content?.ToString() ?? "" : Items[i]?.ToString() ?? "";
            string? icon = navigation && Items[i] is Ui.ListBoxItem { Tag: string glyph } ? glyph : null;
            frame.Labels.Add(new(label, CellBounds(track, cellLength, i), clip, 12.5, icon, IsSelected: !navigation && i == SelectedIndex));
        }
        double selected = Math.Clamp(gesture.Position, 0, 1) * (Items.Count - 1);
        var indicator = CellBounds(track, cellLength, selected);
        float lensScaleX = gesture.ScaleX(10), lensScaleY = gesture.ScaleY(10);
        if (navigation) (lensScaleX, lensScaleY) = (lensScaleY, lensScaleX);
        else
        {
            // Use the page gutters for the expanded lens while retaining vertical scroll clipping.
            clip.Inflate(12, 0);
            double centerX = indicator.Left + indicator.Width / 2;
            double available = Math.Min(centerX - clip.Left, clip.Right - centerX);
            lensScaleX = Math.Min(lensScaleX, (float)Math.Max(1, 2 * (available - .5) / indicator.Width));
        }
        float refractionHeight = 10 * p * opticsScale;
        // The wider rim and shallower offset keep the track outline in the lens across several pixels.
        float refractionAmount = (navigation ? -14 : -8) * p * opticsScale;
        frame.Shape(frame.Lenses, indicator, clip, new(0, 0, 0, .10f - .07f * p), (float)indicator.Height / 2,
            new(refractionHeight, refractionAmount, 0), new(lensScaleX, lensScaleY, (navigation ? .50f : .80f) * p, navigation ? 1 : 0));
        return active;
    }

    private W.Rect CellBounds(W.Rect track, double length, double index) => navigation
        ? new(track.X, track.Y + length * index, track.Width, length)
        : new(track.X + length * index, track.Y, length, track.Height);
}
