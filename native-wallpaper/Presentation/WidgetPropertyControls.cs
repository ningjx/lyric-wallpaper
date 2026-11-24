using W = System.Windows;
using Controls = System.Windows.Controls;
using Fluent = Wpf.Ui.Controls;

namespace LyricWallpaper.Presentation;

internal static class WidgetPropertyControls
{
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
