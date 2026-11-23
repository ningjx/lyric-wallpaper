using System.Globalization;

namespace LyricWallpaper.App;

internal sealed record LaunchOptions(bool Demo, bool Still, bool Preview, bool Settings, bool EditWidgets,
    bool TestScroll, double Duration, int? Fps, string? ServicePath, string? WallpaperPath, string? CapturePath)
{
    public static LaunchOptions Parse(string[] args)
    {
        bool still = args.Contains("--still"), demo = still || args.Contains("--demo");
        var seconds = Value(args, "--seconds");
        double duration = seconds is not null ? double.Parse(seconds, CultureInfo.InvariantCulture)
            : demo ? 60 : double.PositiveInfinity;
        if (seconds is not null && (!double.IsFinite(duration) || duration <= 0))
            throw new ArgumentOutOfRangeException("--seconds");
        var fpsValue = Value(args, "--fps");
        int? fps = fpsValue is null ? null : int.Parse(fpsValue, CultureInfo.InvariantCulture);
        if (fps is < 1 or > 360) throw new ArgumentOutOfRangeException("--fps");
        return new(demo, still, args.Contains("--preview"), args.Contains("--settings"), args.Contains("--edit-widgets"),
            args.Contains("--test-scroll"), duration, fps, Value(args, "--config"), Value(args, "--wallpaper-config"),
            Value(args, "--capture"));
    }

    public static string? Value(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index < 0 ? null : index + 1 < args.Length ? args[index + 1] :
            throw new ArgumentException($"Missing value for {name}");
    }
}
