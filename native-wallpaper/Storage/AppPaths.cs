namespace LyricWallpaper.Storage;

internal sealed record AppPaths(string Wallpaper, string Widgets, string LegacyClock, string Playback, string Logs)
{
    public static AppPaths Resolve(string? servicePath, string? wallpaperPath)
    {
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LyricServer");
        string service = servicePath is null ? Path.Combine(local, "config.json") : Path.GetFullPath(servicePath);
        string wallpaper = wallpaperPath is null ? Path.Combine(local, "wallpaper.json") : Path.GetFullPath(wallpaperPath);
        string widgets = wallpaperPath is null ? Path.Combine(local, "widgets.json") : Path.ChangeExtension(wallpaper, "widgets.json");
        return new(wallpaper, widgets, Path.ChangeExtension(wallpaper, "clock.json"), service,
            Path.Combine(Path.GetDirectoryName(service)!, "logs"));
    }
}
