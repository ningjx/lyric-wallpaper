namespace LyricWallpaper.Wallpaper;

internal static class BackgroundRotation
{
    private static readonly HashSet<string> Extensions = new(
        [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff"], StringComparer.OrdinalIgnoreCase);

    public static (Bitmap? Image, string? File) Pick(WallpaperSettings settings, string? current)
    {
        if (settings.BackgroundDirectory.Length == 0) return default;
        try
        {
            var files = Directory.EnumerateFiles(settings.BackgroundDirectory)
                .Where(file => Extensions.Contains(Path.GetExtension(file))).ToArray();
            Random.Shared.Shuffle(files);
            foreach (string file in files)
            {
                if (string.Equals(file, current, StringComparison.OrdinalIgnoreCase)) continue;
                try { return (BackgroundImages.Load(settings, file), file); }
                catch (Exception ex) when (IsImageError(ex)) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return default;
    }

    internal static bool IsImageError(Exception ex) => ex is IOException or ArgumentException or UnauthorizedAccessException
        or OutOfMemoryException or System.Runtime.InteropServices.ExternalException;
}
