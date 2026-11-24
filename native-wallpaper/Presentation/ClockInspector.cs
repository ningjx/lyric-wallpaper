using W = System.Windows;
using Controls = System.Windows.Controls;
using Media = System.Windows.Media;
using Fluent = Wpf.Ui.Controls;
using LyricWallpaper.Widgets;
using LyricWallpaper.Widgets.Clock;

namespace LyricWallpaper.Presentation;

internal interface IWidgetInspector
{
    W.FrameworkElement View { get; }
    void Display(WidgetOptions options);
}

internal static class WidgetInspectors
{
    public static IWidgetInspector? Create(string type, Action<WidgetOptions> change) =>
        type == ClockDefinition.TypeId ? new ClockInspector(change) : null;
}

internal sealed class ClockInspector : IWidgetInspector
{
    private readonly Controls.StackPanel panel = new();
    private readonly Controls.Slider padding;
    private readonly Fluent.ToggleSwitch format = WidgetPropertyControls.Toggle("24 小时制");
    private readonly Fluent.ToggleSwitch date = WidgetPropertyControls.Toggle("显示日期");
    private readonly Fluent.ToggleSwitch autoInvert = WidgetPropertyControls.Toggle("文字自动反色");
    private readonly Controls.WrapPanel palette = new() { MinHeight = 28, Margin = new W.Thickness(0, 3, 0, 0) };
    private readonly Dictionary<int, Controls.RadioButton> colors = new();
    private ClockOptions options = new();
    private bool updating;
    public W.FrameworkElement View => panel;

    public ClockInspector(Action<WidgetOptions> change)
    {
        panel.Children.Add(WidgetPropertyControls.Range("内边距", 0, 64, "{0:0}", out padding));
        padding.ValueChanged += (_, e) => Set(options with { Padding = (int)Math.Round(e.NewValue) });

        var flags = new Controls.WrapPanel();
        format.Margin = new W.Thickness(0, 0, 14, 0);
        format.Checked += (_, _) => Set(options with { Use24Hour = true });
        format.Unchecked += (_, _) => Set(options with { Use24Hour = false });
        date.Checked += (_, _) => Set(options with { ShowDate = true });
        date.Unchecked += (_, _) => Set(options with { ShowDate = false });
        flags.Children.Add(format);
        flags.Children.Add(date);
        panel.Children.Add(flags);

        autoInvert.ToolTip = "亮背景用黑字，暗背景用白字。关闭后恢复手选颜色。";
        autoInvert.Checked += (_, _) => Set(options with { AutoInvertText = true });
        autoInvert.Unchecked += (_, _) => Set(options with { AutoInvertText = false });
        panel.Children.Add(autoInvert);

        var label = WidgetPropertyControls.Label("文字颜色");
        label.Width = 66;
        palette.Children.Add(label);
        foreach (var (name, argb) in new (string, uint)[]
        {
            ("冰白", 0xFFF0FBFF), ("金黄", 0xFFFFD797), ("薄荷", 0xFFA9F3DD), ("浅蓝", 0xFFBADAFF), ("墨灰", 0xFF20252D)
        })
        {
            int color = unchecked((int)argb);
            var swatch = new Controls.RadioButton
            {
                GroupName = "WidgetTextColor", ToolTip = name, Background = Brush(argb),
                VerticalAlignment = W.VerticalAlignment.Center
            };
            swatch.SetResourceReference(W.FrameworkElement.StyleProperty, "WidgetColorSwatchStyle");
            W.Automation.AutomationProperties.SetName(swatch, name);
            swatch.Checked += (_, _) => Set(options with { TextColor = color });
            colors.Add(color, swatch);
            palette.Children.Add(swatch);
        }
        panel.Children.Add(palette);

        void Set(ClockOptions next)
        {
            if (!updating) change(next);
        }
    }

    public void Display(WidgetOptions widget)
    {
        options = (ClockOptions)widget;
        updating = true;
        try
        {
            padding.Value = options.Padding;
            format.IsChecked = options.Use24Hour;
            date.IsChecked = options.ShowDate;
            autoInvert.IsChecked = options.AutoInvertText;
            palette.IsEnabled = !options.AutoInvertText;
            foreach (var (color, swatch) in colors)
                swatch.IsChecked = color == options.TextColor;
        }
        finally { updating = false; }
    }

    private static Media.SolidColorBrush Brush(uint argb)
    {
        var brush = new Media.SolidColorBrush(Media.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        brush.Freeze();
        return brush;
    }
}
