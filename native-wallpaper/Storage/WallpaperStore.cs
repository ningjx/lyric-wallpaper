using System.Text.Json;
using LyricWallpaper.Wallpaper;

namespace LyricWallpaper.Storage;

internal sealed class WallpaperStore(string path)
{
    public WallpaperSettings Load()
    {
        try
        {
            if (File.Exists(path))
                return (JsonSerializer.Deserialize<WallpaperSettings>(File.ReadAllText(path)) ?? new()).Normalize();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new();
    }

    public void Save(WallpaperSettings settings) =>
        AtomicJsonFile.Save(path, JsonSerializer.Serialize(settings, AtomicJsonFile.Options));
}
