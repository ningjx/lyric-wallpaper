using System.Globalization;
using W = System.Windows;
using Ui = System.Windows.Controls;
using Input = System.Windows.Input;
using Media = System.Windows.Media;
using Fluent = Wpf.Ui.Controls;

namespace LyricWallpaper.Presentation;

internal sealed partial class ColorPickerWindow : Fluent.FluentWindow, INativeGlassHost
{
    private readonly NativeGlassScene glassScene;
    private readonly Media.SolidColorBrush hueBrush = new();
    private readonly Media.SolidColorBrush previewBrush = new();
    private readonly Media.GradientStop lowerOpacity = new() { Offset = 0 };
    private readonly Media.GradientStop upperOpacity = new() { Offset = 1 };
    private readonly int storedAlpha;
    private readonly NumberStyles opacityNumberStyle;
    private Media.Color color;
    private double saturation, brightness;
    private bool updatingInputs, validHex = true, validOpacity = true;

    public int SelectedArgb => storedAlpha | (color.R << 16) | (color.G << 8) | color.B;
    public double SelectedOpacity => OpacitySlider.Value;

    public ColorPickerWindow(string title, int argb, double opacity, double minimum, double maximum,
        string? colorLockReason, bool wholePercent)
    {
        InitializeComponent();
        SettingsTheme.Apply(this, glassControls: true);
        FontFamily = SettingsTheme.FontFamily;
        FontSize = SettingsTheme.FontSize;
        Title = PickerTitle.Title = title;
        storedAlpha = argb & unchecked((int)0xFF000000);
        opacityNumberStyle = wholePercent ? NumberStyles.None : NumberStyles.AllowDecimalPoint;
        ColorPlane.Background = hueBrush;
        PreviewSwatch.Background = previewBrush;
        OpacitySlider.Background = new Media.LinearGradientBrush(
            new Media.GradientStopCollection { lowerOpacity, upperOpacity }, new W.Point(0, 0), new W.Point(1, 0));
        OpacitySlider.Minimum = minimum;
        OpacitySlider.Maximum = maximum;
        OpacitySlider.Value = opacity;
        OpacityInput.Text = FormatOpacity(SelectedOpacity);
        OpacityInput.ToolTip = $"不透明度范围 {FormatOpacity(minimum)}–{FormatOpacity(maximum)}";
        if (wholePercent) OpacityInput.ToolTip += "，请输入整数";
        HexInput.Text = FormatHex(argb);
        ReadRgb(Rgb(argb));

        if (colorLockReason is not null)
        {
            ColorControls.Visibility = W.Visibility.Collapsed;
            ColorLockNote.Text = colorLockReason;
            ColorLockNote.Visibility = W.Visibility.Visible;
            HexInput.IsReadOnly = true;
            HexInput.IsTabStop = false;
        }

        ColorPlane.SizeChanged += (_, _) => PositionMarker();
        ColorPlane.MouseLeftButtonDown += (_, e) =>
        {
            ColorPlane.Focus();
            ColorPlane.CaptureMouse();
            ColorFromPoint(e.GetPosition(ColorPlane));
            e.Handled = true;
        };
        ColorPlane.MouseMove += (_, e) =>
        {
            if (ColorPlane.IsMouseCaptured && e.LeftButton == Input.MouseButtonState.Pressed)
                ColorFromPoint(e.GetPosition(ColorPlane));
        };
        ColorPlane.MouseLeftButtonUp += (_, e) =>
        {
            if (!ColorPlane.IsMouseCaptured) return;
            ColorFromPoint(e.GetPosition(ColorPlane));
            ColorPlane.ReleaseMouseCapture();
            e.Handled = true;
        };
        ColorPlane.KeyDown += AdjustPlane;
        HueSlider.ValueChanged += (_, _) =>
        {
            if (!updatingInputs) UpdateFromPlane();
        };
        OpacitySlider.ValueChanged += (_, _) =>
        {
            if (updatingInputs) return;
            updatingInputs = true;
            OpacityInput.Text = FormatOpacity(SelectedOpacity);
            updatingInputs = false;
            validOpacity = true;
            RefreshValidation(OpacityInput, validOpacity);
            RefreshOpacity();
        };
        HexInput.TextChanged += (_, _) => ReadHex();
        OpacityInput.TextChanged += (_, _) => ReadOpacity();
        glassScene = new(this, (W.FrameworkElement)Content);
    }

    public void InvalidateGlass() => glassScene?.Invalidate();

    private void ColorFromPoint(W.Point point)
    {
        saturation = Math.Clamp(point.X / ColorPlane.ActualWidth, 0, 1);
        brightness = Math.Clamp(1 - point.Y / ColorPlane.ActualHeight, 0, 1);
        UpdateFromPlane();
    }

    private void AdjustPlane(object sender, Input.KeyEventArgs e)
    {
        const double step = .01;
        switch (e.Key)
        {
            case Input.Key.Left: saturation = Math.Max(0, saturation - step); break;
            case Input.Key.Right: saturation = Math.Min(1, saturation + step); break;
            case Input.Key.Down: brightness = Math.Max(0, brightness - step); break;
            case Input.Key.Up: brightness = Math.Min(1, brightness + step); break;
            default: return;
        }
        UpdateFromPlane();
        e.Handled = true;
    }

    private void UpdateFromPlane()
    {
        color = FromHsv(HueSlider.Value, saturation, brightness);
        updatingInputs = true;
        HexInput.Text = FormatHex(SelectedArgb);
        updatingInputs = false;
        validHex = true;
        RefreshValidation(HexInput, validHex);
        RefreshColor();
    }

    private void ReadHex()
    {
        if (updatingInputs) return;
        ReadOnlySpan<char> hex = HexInput.Text.AsSpan().Trim();
        if (!hex.IsEmpty && hex[0] == '#') hex = hex[1..];
        int rgb = 0;
        validHex = hex.Length == 6 && int.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out rgb);
        RefreshValidation(HexInput, validHex);
        if (validHex) ReadRgb(Rgb(rgb));
    }

    private void ReadOpacity()
    {
        if (updatingInputs) return;
        ReadOnlySpan<char> text = OpacityInput.Text.AsSpan().Trim();
        if (!text.IsEmpty && text[^1] is '%' or '％') text = text[..^1].TrimEnd();
        bool parsed = double.TryParse(text, opacityNumberStyle, CultureInfo.InvariantCulture, out double percent);
        double opacity = percent / 100;
        validOpacity = parsed && opacity >= OpacitySlider.Minimum && opacity <= OpacitySlider.Maximum;
        RefreshValidation(OpacityInput, validOpacity);
        if (!validOpacity) return;
        updatingInputs = true;
        OpacitySlider.Value = opacity;
        updatingInputs = false;
        RefreshOpacity();
    }

    private void RefreshValidation(Ui.Control input, bool valid)
    {
        if (valid) input.ClearValue(Ui.Control.BorderBrushProperty);
        else input.SetResourceReference(Ui.Control.BorderBrushProperty, "SystemFillColorCriticalBrush");
        ApplyButton.IsEnabled = validHex && validOpacity;
    }

    private void ReadRgb(Media.Color next)
    {
        color = next;
        double red = color.R / 255d, green = color.G / 255d, blue = color.B / 255d;
        brightness = Math.Max(red, Math.Max(green, blue));
        double range = brightness - Math.Min(red, Math.Min(green, blue));
        saturation = brightness == 0 ? 0 : range / brightness;
        if (range > 0)
        {
            double sector = brightness == red ? (green - blue) / range
                : brightness == green ? 2 + (blue - red) / range : 4 + (red - green) / range;
            updatingInputs = true;
            HueSlider.Value = (sector * 60 + 360) % 360;
            updatingInputs = false;
        }
        RefreshColor();
    }

    private void RefreshColor()
    {
        hueBrush.Color = FromHsv(HueSlider.Value, 1, 1);
        lowerOpacity.Color = WithOpacity(color, OpacitySlider.Minimum);
        upperOpacity.Color = WithOpacity(color, OpacitySlider.Maximum);
        PositionMarker();
        RefreshOpacity();
    }

    private void RefreshOpacity() => previewBrush.Color = WithOpacity(color, SelectedOpacity);

    private void PositionMarker()
    {
        Ui.Canvas.SetLeft(Marker, saturation * ColorPlane.ActualWidth - Marker.Width / 2);
        Ui.Canvas.SetTop(Marker, (1 - brightness) * ColorPlane.ActualHeight - Marker.Height / 2);
    }

    private void Apply(object sender, W.RoutedEventArgs e) => DialogResult = true;

    public static Media.Color Rgb(int argb) => Media.Color.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    public static string FormatHex(int argb) => $"#{argb & 0xFFFFFF:X6}";
    public static string FormatOpacity(double opacity) => opacity.ToString("0.##%", CultureInfo.InvariantCulture);

    private static Media.Color WithOpacity(Media.Color rgb, double opacity) =>
        Media.Color.FromArgb((byte)Math.Round(opacity * 255), rgb.R, rgb.G, rgb.B);

    private static Media.Color FromHsv(double hue, double saturation, double brightness)
    {
        double sector = (hue % 360) / 60;
        double chroma = brightness * saturation;
        double secondary = chroma * (1 - Math.Abs(sector % 2 - 1));
        double match = brightness - chroma;
        var (r, g, b) = (int)sector switch
        {
            0 => (chroma, secondary, 0d),
            1 => (secondary, chroma, 0d),
            2 => (0d, chroma, secondary),
            3 => (0d, secondary, chroma),
            4 => (secondary, 0d, chroma),
            _ => (chroma, 0d, secondary)
        };
        return Media.Color.FromRgb((byte)Math.Round((r + match) * 255),
            (byte)Math.Round((g + match) * 255), (byte)Math.Round((b + match) * 255));
    }
}
