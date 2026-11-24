using W = System.Windows;
using Controls = System.Windows.Controls;
using Primitives = System.Windows.Controls.Primitives;
using Media = System.Windows.Media;
using Fluent = Wpf.Ui.Controls;
using LyricWallpaper.Widgets;
using LyricWallpaper.Widgets.Clock;

namespace LyricWallpaper.Presentation;

internal interface IWidgetInspector
{
    W.FrameworkElement View { get; }
    void Fit(double width);
    void Display(WidgetInstance widget);
}

internal static class WidgetInspectors
{
    public static IWidgetInspector? Create(string type, Action<WidgetOptions> change, Action<WidgetVisibility> setVisibility) =>
        type == ClockDefinition.TypeId ? new ClockInspector(change, setVisibility) : null;
}

internal sealed class ClockInspector : IWidgetInspector
{
    private readonly Controls.Grid panel = new();
    private readonly Primitives.UniformGrid flags = new();
    private readonly Controls.ColumnDefinition colorColumn = new();
    private readonly Fluent.ToggleSwitch format = WidgetPropertyControls.Toggle("24 小时制");
    private readonly Fluent.ToggleSwitch date = WidgetPropertyControls.Toggle("显示日期");
    private readonly Fluent.ToggleSwitch autoInvert = WidgetPropertyControls.Toggle("文字自动反色");
    private readonly Fluent.ToggleSwitch idleOnly = WidgetPropertyControls.Toggle("仅闲时显示");
    private readonly Controls.StackPanel palette = new() { VerticalAlignment = W.VerticalAlignment.Center };
    private readonly Controls.TextBlock colorLabel = WidgetPropertyControls.Label("文字颜色");
    private readonly Controls.WrapPanel swatches = new();
    private readonly Dictionary<int, Controls.RadioButton> colors = new();
    private ClockOptions options = new();
    private bool updating;
    private (int Columns, bool SideBySide, bool ColorsInline)? currentLayout;
    public W.FrameworkElement View => panel;

    public ClockInspector(Action<WidgetOptions> change, Action<WidgetVisibility> setVisibility)
    {
        format.Checked += (_, _) => Set(options with { Use24Hour = true });
        format.Unchecked += (_, _) => Set(options with { Use24Hour = false });
        date.Checked += (_, _) => Set(options with { ShowDate = true });
        date.Unchecked += (_, _) => Set(options with { ShowDate = false });
        autoInvert.ToolTip = "亮背景用黑字，暗背景用白字。关闭后恢复手选颜色。";
        autoInvert.Checked += (_, _) => Set(options with { AutoInvertText = true });
        autoInvert.Unchecked += (_, _) => Set(options with { AutoInvertText = false });
        idleOnly.Checked += (_, _) => { if (!updating) setVisibility(WidgetVisibility.Idle); };
        idleOnly.Unchecked += (_, _) => { if (!updating) setVisibility(WidgetVisibility.Always); };

        flags.Children.Add(format);
        flags.Children.Add(date);
        flags.Children.Add(autoInvert);
        flags.Children.Add(idleOnly);
        panel.RowDefinitions.Add(new() { Height = W.GridLength.Auto });
        panel.RowDefinitions.Add(new() { Height = W.GridLength.Auto });
        panel.ColumnDefinitions.Add(new());
        panel.ColumnDefinitions.Add(colorColumn);
        panel.Children.Add(flags);

        palette.Children.Add(colorLabel);
        palette.Children.Add(swatches);
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
            swatches.Children.Add(swatch);
        }
        panel.Children.Add(palette);
        Fit(0);

        void Set(ClockOptions next)
        {
            if (!updating) change(next);
        }
    }

    public void Fit(double width)
    {
        bool sideBySide = width >= 420;
        bool colorsInline = !sideBySide && width >= 206;
        int columns = width >= 248 ? 2 : 1;
        if (currentLayout == (columns, sideBySide, colorsInline)) return;
        currentLayout = (columns, sideBySide, colorsInline);
        flags.Columns = columns;
        colorColumn.Width = new W.GridLength(sideBySide ? 160 : 0);
        Controls.Grid.SetColumn(palette, sideBySide ? 1 : 0);
        Controls.Grid.SetRow(palette, sideBySide ? 0 : 1);
        palette.Margin = sideBySide ? new W.Thickness(14, 0, 0, 0) : new W.Thickness(0, 4, 0, 0);
        palette.Orientation = colorsInline ? Controls.Orientation.Horizontal : Controls.Orientation.Vertical;
        colorLabel.Margin = colorsInline ? new W.Thickness(0, 0, 10, 0) : new W.Thickness(0, 0, 0, 4);
    }

    public void Display(WidgetInstance widget)
    {
        options = (ClockOptions)widget.Options;
        updating = true;
        try
        {
            format.IsChecked = options.Use24Hour;
            date.IsChecked = options.ShowDate;
            autoInvert.IsChecked = options.AutoInvertText;
            idleOnly.IsChecked = widget.Visibility == WidgetVisibility.Idle;
            swatches.IsEnabled = !options.AutoInvertText;
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
