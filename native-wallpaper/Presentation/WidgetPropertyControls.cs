using W = System.Windows;
using Controls = System.Windows.Controls;
using Data = System.Windows.Data;
using Media = System.Windows.Media;
using Fluent = Wpf.Ui.Controls;

namespace LyricWallpaper.Presentation;

internal static class WidgetPropertyControls
{
    private static readonly Media.FontFamily ValueFont = new("Cascadia Mono, Consolas");

    public static Controls.Grid Range(string label, double min, double max, string format, out Controls.Slider slider)
    {
        slider = new()
        {
            Minimum = min, Maximum = max, TickFrequency = 1, SmallChange = 1, LargeChange = 5,
            IsSnapToTickEnabled = true, IsMoveToPointEnabled = true, VerticalAlignment = W.VerticalAlignment.Center
        };
        slider.SetResourceReference(W.FrameworkElement.StyleProperty, "SettingsSliderStyle");
        W.Automation.AutomationProperties.SetName(slider, label);
        var value = new Controls.TextBlock
        {
            FontFamily = ValueFont, FontSize = 12, TextAlignment = W.TextAlignment.Right,
            VerticalAlignment = W.VerticalAlignment.Center
        };
        value.SetBinding(Controls.TextBlock.TextProperty, new Data.Binding(nameof(Controls.Slider.Value))
        {
            Source = slider, StringFormat = format
        });
        var row = new Controls.Grid { MinHeight = 28 };
        row.ColumnDefinitions.Add(new() { Width = new W.GridLength(66) });
        row.ColumnDefinitions.Add(new());
        row.ColumnDefinitions.Add(new() { Width = new W.GridLength(45) });
        row.Children.Add(Label(label));
        Controls.Grid.SetColumn(slider, 1);
        Controls.Grid.SetColumn(value, 2);
        row.Children.Add(slider);
        row.Children.Add(value);
        return row;
    }

    public static Fluent.ToggleSwitch Toggle(string label)
    {
        var toggle = new Fluent.ToggleSwitch
        {
            Content = label, MinHeight = 28, VerticalContentAlignment = W.VerticalAlignment.Center
        };
        W.Automation.AutomationProperties.SetName(toggle, label);
        return toggle;
    }

    public static Controls.TextBlock Label(string text) => new()
    {
        Text = text, VerticalAlignment = W.VerticalAlignment.Center
    };
}
