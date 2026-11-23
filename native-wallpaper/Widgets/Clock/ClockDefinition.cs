using System.Globalization;
using System.Text.Json;
using LyricWallpaper.Scene;
using LyricWallpaper.Widgets;

namespace LyricWallpaper.Widgets.Clock;

internal sealed record ClockOptions : WidgetOptions
{
    public bool ShowDate { get; init; } = true;
    public bool Use24Hour { get; init; } = true;
    public int TextColor { get; init; } = unchecked((int)0xFFF0FBFF);
    public int Padding { get; init; } = 17;

    public ClockOptions Normalize() => this with { Padding = Math.Clamp(Padding, 0, 64) };
}

internal sealed class ClockDefinition : WidgetDefinition
{
    private static readonly CultureInfo Chinese = CultureInfo.GetCultureInfo("zh-CN");
    private static readonly Guid InitialId = new("f492e366-d9fc-4876-9f6f-fa0baddc65d0");
    private const string FontFamily = "Microsoft YaHei UI";
    public const string TypeId = "clock";
    public override string Type => TypeId;
    public override string Title => "时钟";
    public override WidgetVisibility DefaultVisibility => WidgetVisibility.Always;
    public override WidgetOptions DefaultOptions { get; } = new ClockOptions();

    public static WidgetDocument InitialDocument(WidgetPlacement? placement = null, ClockOptions? options = null) =>
        new([new(InitialId, TypeId, (placement ?? WidgetPlacement.Default).Normalize(), WidgetVisibility.Idle,
            (options ?? new ClockOptions()).Normalize())]);

    public override WidgetOptions ReadOptions(JsonElement element) =>
        (element.ValueKind == JsonValueKind.Object ? element.Deserialize<ClockOptions>() : null)?.Normalize() ?? DefaultOptions;

    public override WidgetOptions NormalizeOptions(WidgetOptions options) => options is ClockOptions clock ? clock.Normalize() : DefaultOptions;

    public override Size EstimateSize(Rectangle display) =>
        new((int)Math.Ceiling(display.Width * .19), (int)Math.Ceiling(display.Height * .18));

    public override WidgetContent Read(WidgetOptions options, DateTime time)
    {
        var clock = (ClockOptions)options;
        var minute = time.AddTicks(-(time.Ticks % TimeSpan.TicksPerMinute));
        var text = new SceneText(
            time.ToString(clock.Use24Hour ? "HH:mm" : "tt h:mm", Chinese),
            clock.ShowDate ? time.ToString("M月d日 dddd", Chinese) : null,
            FontFamily);
        return new(text, clock.TextColor, clock.Padding, minute.AddMinutes(1));
    }
}
