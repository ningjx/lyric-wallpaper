using System.Text.Json;
using LyricWallpaper.Scene;
using LyricWallpaper.Widgets.Clock;

namespace LyricWallpaper.Widgets;

internal sealed class WidgetCatalog
{
    private readonly Dictionary<string, WidgetDefinition> byType;
    public IReadOnlyList<WidgetDefinition> Definitions { get; }

    public WidgetCatalog()
    {
        Definitions = [new ClockDefinition()];
        byType = Definitions.ToDictionary(item => item.Type, StringComparer.Ordinal);
    }

    public WidgetDefinition? Find(string type) => byType.GetValueOrDefault(type);

    public WidgetInstance Create(WidgetDefinition definition, WidgetPosition position) =>
        new(Guid.NewGuid(), definition.Type, WidgetPlacement.Default with { Position = position },
            definition.DefaultVisibility, definition.DefaultOptions);

    public WidgetDocument DefaultDocument() => ClockDefinition.InitialDocument();
}

internal abstract class WidgetDefinition
{
    public abstract string Type { get; }
    public abstract string Title { get; }
    public abstract string Category { get; }
    public abstract string Icon { get; }
    public abstract WidgetVisibility DefaultVisibility { get; }
    public abstract WidgetOptions DefaultOptions { get; }
    public abstract WidgetOptions NormalizeOptions(WidgetOptions options);
    public abstract WidgetOptions ReadOptions(JsonElement element);
    public abstract WidgetContent Read(WidgetOptions options, DateTime time);
    public abstract Size EstimateSize(Rectangle display);
}

internal readonly record struct WidgetContent(SceneText Text, int Color, int Padding, DateTime RefreshAt);
