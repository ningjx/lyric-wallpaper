using System.Text.Json;
using System.Text.Json.Nodes;

namespace LyricWallpaper.Storage;

internal sealed class PlaybackSettingsStore(AppPaths paths)
{
    public string LogDirectory => paths.Logs;

    public JsonObject Load() => File.Exists(paths.Playback)
        ? JsonNode.Parse(File.ReadAllText(paths.Playback)) as JsonObject ?? throw new JsonException("歌词服务配置需要是 JSON 对象。")
        : new JsonObject();

    public bool Edit(Action<JsonObject> edit)
    {
        var document = Load();
        var original = document.DeepClone();
        edit(document);
        if (JsonNode.DeepEquals(original, document)) return false;
        AtomicJsonFile.Save(paths.Playback, document.ToJsonString(AtomicJsonFile.Options));
        return true;
    }

    public static JsonObject Section(JsonObject document, string key)
    {
        if (document[key] is JsonObject section) return section;
        if (document[key] is not null) throw new JsonException($"配置中的 {key} 需要是 JSON 对象。");
        section = new JsonObject();
        document[key] = section;
        return section;
    }

    public static T Read<T>(JsonObject? section, string key, T fallback) =>
        section?[key] is JsonValue value && value.TryGetValue<T>(out var result) ? result : fallback;
}
