using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LyricWallpaper.Playback;

namespace LyricWallpaper.Playback.Resolution;

internal sealed record LyricsResult(
    string Provider, bool HasLyric, string Lrc = "", string TranslatedLyric = "", string KaraokeLyric = "",
    Dictionary<string, string>? Meta = null, [property: JsonIgnore] int Similarity = 0)
{
    public int Completeness() => (Lrc.Length == 0 ? 0 : 1) + (TranslatedLyric.Length == 0 ? 0 : 1) + (KaraokeLyric.Length == 0 ? 0 : 1);
}

internal sealed class LyricCache(string directory, int capacity)
{
    private readonly Dictionary<SongKey, LinkedListNode<(SongKey Key, LyricsResult Result)>> entries = new();
    private readonly LinkedList<(SongKey Key, LyricsResult Result)> order = new();

    public LyricsResult? Get(SongKey key)
    {
        lock (entries) return Read(key);
    }

    private LyricsResult? Read(SongKey key)
    {
        if (entries.TryGetValue(key, out var entry))
        {
            order.Remove(entry);
            order.AddLast(entry);
            return entry.Value.Result;
        }
        try
        {
            var result = JsonSerializer.Deserialize<LyricsResult>(File.ReadAllText(CachePath(key)), ServiceOptions.Json);
            if (result is not { HasLyric: true } || string.IsNullOrEmpty(result.Lrc)) return null;
            result = result with { TranslatedLyric = result.TranslatedLyric ?? "", KaraokeLyric = result.KaraokeLyric ?? "" };
            Remember(key, result);
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Put(SongKey key, LyricsResult result)
    {
        lock (entries)
        {
            Remember(key, result);
            try { WriteJson(CachePath(key), result); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private void Remember(SongKey key, LyricsResult result)
    {
        if (entries.Remove(key, out var old)) order.Remove(old);
        entries.Add(key, order.AddLast((key, result)));
        if (entries.Count <= Math.Max(8, capacity)) return;
        entries.Remove(order.First!.Value.Key);
        order.RemoveFirst();
    }

    private string CachePath(SongKey key)
    {
        string hash = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(key.Title + "|" + key.Artist)));
        return Path.Combine(directory, hash[..16] + ".json");
    }

    internal static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, ServiceOptions.Json));
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}

internal readonly record struct SongKey(string Title, string Artist)
{
    public static SongKey Normalize(string title, string artist) => new(title.Trim().ToLowerInvariant(), artist.Trim().ToLowerInvariant());
}
