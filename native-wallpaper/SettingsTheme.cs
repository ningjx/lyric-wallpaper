using W = System.Windows;
using Media = System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;

internal static class SettingsTheme
{
    public static readonly Media.FontFamily FontFamily = new("Segoe UI, Microsoft YaHei UI");
    public const double FontSize = 13;

    public static void Apply(W.FrameworkElement element)
    {
        element.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = ApplicationTheme.Light });
        element.Resources.MergedDictionaries.Add(new ControlsDictionary());
        element.Resources.MergedDictionaries.Add(new W.ResourceDictionary
        {
            Source = new Uri("/NativeWallpaper;component/SettingsStyles.xaml", UriKind.Relative)
        });
        element.Resources["ControlCornerRadius"] = new W.CornerRadius(6);
    }
}
