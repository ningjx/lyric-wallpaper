using System.Text.Json;

namespace LyricWallpaper.Storage;

internal static class AtomicJsonFile
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static void Save(string path, string contents, bool retrySharing = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, contents);
            for (int delay = 12; ; delay *= 2)
            {
                try
                {
                    File.Move(temporary, path, overwrite: true);
                    break;
                }
                catch (Exception ex) when (retrySharing && delay <= 48 &&
                    (ex is UnauthorizedAccessException || ex is IOException && (ex.HResult & 0xFFFF) is 32 or 33))
                {
                    Thread.Sleep(delay);
                }
            }
        }
        finally { File.Delete(temporary); }
    }
}
