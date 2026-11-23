using System.Collections.Immutable;
using System.Text.Json;

namespace LyricWallpaper.Widgets;

internal enum WidgetVisibility { Idle, Always }

internal readonly record struct WidgetPlacement(WidgetPosition Position, double Scale)
{
    public const double MinScale = .35, MaxScale = 2.2;
    public static WidgetPlacement Default => new(WidgetPosition.Center, 1);

    public WidgetPlacement Normalize() => new(
        Position.X is >= 0 and <= 1 && Position.Y is >= 0 and <= 1 ? Position : WidgetPosition.Center,
        double.IsFinite(Scale) ? Math.Clamp(Scale, MinScale, MaxScale) : 1);
}

internal abstract record WidgetOptions;

internal sealed record UnknownWidgetOptions(JsonElement Instance) : WidgetOptions;

internal sealed record WidgetInstance(Guid Id, string Type, WidgetPlacement Placement, WidgetVisibility Visibility, WidgetOptions Options);

internal sealed record WidgetDocument(ImmutableArray<WidgetInstance> Items, string? ReadOnlyReason = null);
