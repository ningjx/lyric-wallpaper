using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using LyricWallpaper.Widgets;
using LyricWallpaper.Widgets.Clock;

namespace LyricWallpaper.Storage;

internal sealed class WidgetStore(AppPaths paths, WidgetCatalog catalog)
{
    private const int CurrentVersion = 3;

    public WidgetDocument Load()
    {
        try
        {
            if (File.Exists(paths.Widgets))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(paths.Widgets));
                return ReadDocument(json.RootElement);
            }
            if (File.Exists(paths.LegacyClock))
            {
                var position = JsonSerializer.Deserialize<WidgetPosition>(File.ReadAllText(paths.LegacyClock));
                return ClockDefinition.InitialDocument(WidgetPlacement.Default with { Position = position });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException
            or KeyNotFoundException or FormatException)
        {
            return new([], "控件配置无法读取。保留原文件以便恢复。");
        }
        return catalog.DefaultDocument();
    }

    public void Save(WidgetDocument document)
    {
        if (document.ReadOnlyReason is { } reason) throw new IOException(reason);
        if (File.Exists(paths.Widgets))
        {
            int version;
            try
            {
                using var current = JsonDocument.Parse(File.ReadAllText(paths.Widgets));
                version = VersionOf(current.RootElement);
            }
            catch (JsonException ex) { throw new IOException("控件配置无法读取。", ex); }
            if (version < 1 || version > CurrentVersion)
                throw new IOException("控件配置的版本无法写入。");
            if (version < CurrentVersion)
            {
                string backup = paths.Widgets + $".v{version}.bak";
                if (!File.Exists(backup)) File.Copy(paths.Widgets, backup);
            }
        }

        var items = new JsonArray();
        foreach (var item in document.Items)
        {
            if (item.Options is UnknownWidgetOptions unknown)
            {
                items.Add(JsonNode.Parse(unknown.Instance.GetRawText()));
                continue;
            }
            var options = JsonSerializer.SerializeToNode(item.Options, item.Options.GetType());
            items.Add(new JsonObject
            {
                ["Id"] = item.Id.ToString(),
                ["Type"] = item.Type,
                ["Placement"] = JsonSerializer.SerializeToNode(item.Placement),
                ["Visibility"] = item.Visibility == WidgetVisibility.Always ? "always" : "idle",
                ["Options"] = options
            });
        }
        var root = new JsonObject { ["Version"] = CurrentVersion, ["Items"] = items };
        AtomicJsonFile.Save(paths.Widgets, root.ToJsonString(AtomicJsonFile.Options), retrySharing: true);
    }

    private WidgetDocument ReadDocument(JsonElement root)
    {
        switch (VersionOf(root))
        {
            case 1:
                return ClockDefinition.InitialDocument(Placement(root.GetProperty("Clock")), ClockStyle(root.GetProperty("Clock")));
            case 2:
                return ReadList(root.GetProperty("Clocks"), item =>
                {
                    var settings = item.GetProperty("Settings");
                    return new(item.GetProperty("Id").GetGuid(), ClockDefinition.TypeId, Placement(settings), WidgetVisibility.Idle, ClockStyle(settings));
                });
            case CurrentVersion:
                return ReadList(root.GetProperty("Items"), ReadInstance);
            default:
                return new([], "控件配置来自不兼容的版本。");
        }
    }

    private static WidgetDocument ReadList(JsonElement array, Func<JsonElement, WidgetInstance> read)
    {
        var builder = ImmutableArray.CreateBuilder<WidgetInstance>();
        var seen = new HashSet<Guid>();
        foreach (var element in array.EnumerateArray())
        {
            var item = read(element);
            if (item.Id != Guid.Empty && seen.Add(item.Id)) builder.Add(item);
        }
        return new(builder.ToImmutable());
    }

    private WidgetInstance ReadInstance(JsonElement item)
    {
        Guid id = item.GetProperty("Id").GetGuid();
        string type = item.GetProperty("Type").GetString() ?? "";
        if (catalog.Find(type) is not { } definition)
            return new(id, type, WidgetPlacement.Default, WidgetVisibility.Idle, new UnknownWidgetOptions(item.Clone()));
        WidgetPlacement placement = item.TryGetProperty("Placement", out var layout) ? Placement(layout) : WidgetPlacement.Default;
        var visibility = item.TryGetProperty("Visibility", out var value) && value.GetString() == "idle"
            ? WidgetVisibility.Idle : WidgetVisibility.Always;
        WidgetOptions options = item.TryGetProperty("Options", out var settings) ? definition.ReadOptions(settings) : definition.DefaultOptions;
        return new(id, type, placement, visibility, options);
    }

    private static int VersionOf(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Version", out var version)
        && version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out int number) ? number : 0;

    private static WidgetPlacement Placement(JsonElement settings)
    {
        WidgetPosition position = settings.TryGetProperty("Position", out var anchor)
            ? anchor.Deserialize<WidgetPosition>() : WidgetPosition.Center;
        double scale = settings.TryGetProperty("Scale", out var size) ? size.GetDouble() : 1;
        return new WidgetPlacement(position, scale).Normalize();
    }

    private static ClockOptions ClockStyle(JsonElement settings) =>
        (settings.Deserialize<ClockOptions>() ?? new()).Normalize();
}
