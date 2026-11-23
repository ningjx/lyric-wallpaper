using System.Text.Json;
using LyricWallpaper.Platform.Windows;
using LyricWallpaper.Playback;
using LyricWallpaper.Storage;
using LyricWallpaper.Wallpaper;
using LyricWallpaper.Widgets;

namespace LyricWallpaper.App;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [STAThread]
    private static int Main(string[] args)
    {
        string? reportPath = null;
        try
        {
            reportPath = LaunchOptions.Value(args, "--report");
            var options = LaunchOptions.Parse(args);
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            bool first = true;
            using var instance = options.Demo ? null : new Mutex(true, @"Local\LyricWallpaper.Native", out first);
            if (instance is not null && !first)
                throw new InvalidOperationException("歌词壁纸已在运行，可从系统托盘退出。");

            var paths = AppPaths.Resolve(options.ServicePath, options.WallpaperPath);
            var wallpaperStore = new WallpaperStore(paths.Wallpaper);
            var playbackStore = new PlaybackSettingsStore(paths);
            var catalog = new WidgetCatalog();
            var widgetStore = new WidgetStore(paths, catalog);
            using var playback = options.Demo ? null : new LyricsService(paths.Playback, paths.Logs);
            var appearance = !options.Demo || options.WallpaperPath is not null ? wallpaperStore.Load() : new WallpaperSettings();
            WidgetDocument widgets = !options.Demo || options.EditWidgets ? widgetStore.Load() : catalog.DefaultDocument();
            bool testScroll = playback is not null && options.TestScroll;
            RenderReport Render(Action<RenderControls>? ready, Action<PlaybackSnapshot?>? status,
                Action<WidgetEditorLayout>? editor, Action<WidgetHold>? hold, CancellationToken stop) =>
                DesktopScene.Run(options, reportPath is not null, playback, appearance, catalog, widgets, testScroll,
                    ready, status, editor, hold, stop);
            var report = playback is null && !options.EditWidgets ? Render(null, null, null, null, default)
                : DesktopApplication.Run(wallpaperStore, playbackStore, widgetStore, catalog, playback, appearance,
                    testScroll, options.Settings, options.EditWidgets, Render);
            WriteReport(reportPath, report);
            return report.Frames > 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            WriteReport(reportPath, new { Error = ex.ToString(), Win32Error = (ex as System.ComponentModel.Win32Exception)?.NativeErrorCode });
            if (reportPath is null)
                MessageBox.Show(ex.Message, "Native Lyrics Wallpaper", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static void WriteReport(string? path, object report)
    {
        if (path is not null) File.WriteAllText(path, JsonSerializer.Serialize(report, JsonOptions));
    }
}
