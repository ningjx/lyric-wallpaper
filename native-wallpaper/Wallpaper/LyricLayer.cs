using System.Numerics;
using LyricWallpaper.Scene;

namespace LyricWallpaper.Wallpaper;

internal static class LyricLayer
{
    private static readonly string[] Lines =
    [
        "把城市的声音调低", "听见晚风穿过缝隙", "有一颗星落在眼底", "让时间慢慢流成光",
        "我们漂浮在夜色里", "等一场温柔的潮汐", "把没说完的话藏起", "交给遥远的天际",
        "当世界安静了一秒", "月光轻轻落在肩上", "沿着你的目光远行", "直到天色渐渐明亮"
    ];
    private static readonly double[] Times = [0, 5.5, 11, 16.5, 22.5, 28, 33.5, 39, 44.5, 50, 55.5, 61];
    private const double Start = 16.5, Period = 67;
    private const double TestInterval = 3;
    private static readonly LyricLine[] DemoLines = Lines.Select((line, i) => new LyricLine(Times[i], line)).ToArray();
    private static readonly LyricLine[] TestLines = BuildTestLines();

    public static double LeadSeconds(int height, WallpaperSettings settings)
    {
        var gap = RowGap(height, settings);
        double speed = settings.ScrollSpeed;
        double normalized = 0;
        while (normalized < 12 && (gap * (1 + normalized) * Math.Exp(-normalized) > .75 ||
                                   gap * speed * normalized * Math.Exp(-normalized) > 3))
            normalized += .05;
        return normalized / speed + .03;
    }

    public static LyricFrame DemoAt(double elapsed, double lead, bool still, double speed)
    {
        if (still) return new(DemoLines, 3, false, double.PositiveInfinity);
        var time = (Start + elapsed + lead) % Period;
        int active = Array.FindLastIndex(Times, t => t <= time);
        double since = time - Times[active];
        double next = elapsed + (active + 1 < Times.Length ? Times[active + 1] : Period) - time;
        if (elapsed <= since || since >= lead - .03) return new(DemoLines, active, false, next);
        int previous = active == 0 ? Lines.Length - 1 : active - 1;
        return new(DemoLines, active + (previous - active) * (1 + speed * since) * Math.Exp(-speed * since), true, next);
    }

    private static LyricLine[] BuildTestLines()
    {
        string[] captions =
        [
            "Lower the city’s volume", "A breeze slips through the cracks", "A star falls into view",
            "Let time slowly turn to light", "We float in the night", "Wait for a gentler tide",
            "Keep the words we never said", "Carry them to the horizon", "A second of quiet",
            "Moonlight rests on a shoulder", "Follow your gaze into the distance", "Until the sky grows bright"
        ];
        var sequence = Lines.Select((text, i) =>
            new LyricLine(i * TestInterval,
                i == 3 ? "让时间慢慢流成光 · 测试滚动" : text, captions[i])).ToArray();
        // Duplicate the neighboring rows so they stay in place when the test loops.
        return [.. sequence, .. sequence, .. sequence];
    }

    public static LyricFrame TestAt(double elapsed, double lead, WallpaperSettings settings)
    {
        double period = Lines.Length * TestInterval;
        double phase = (elapsed + TestInterval * 3 + lead + settings.OffsetMs / 1000d) % period;
        int line = (int)(phase / TestInterval);
        double since = phase - line * TestInterval;
        double duration = Math.Min(TestInterval, lead);
        int active = Lines.Length + line;
        if (since >= duration)
            return new(TestLines, active, false, elapsed + TestInterval - since);
        double speed = settings.ScrollSpeed;
        double Step(double time) => 1 - (1 + speed * time) * Math.Exp(-speed * time);
        return new(TestLines, active - 1 + Step(since) / Step(duration), true, elapsed + TestInterval - since);
    }

    public static void Collect(LyricFrame frame, WallpaperSettings settings, SceneViewport viewport, ISceneText text, List<GlassCard> cards)
    {
        int width = viewport.Width, height = viewport.Height;
        double focus = frame.Focus;
        float gap = RowGap(height, settings);
        float padding = settings.Padding * height / 1080f;
        int first = Math.Max(0, (int)Math.Ceiling(focus - settings.RenderDistance));
        int end = Math.Min(frame.Lines.Length, (int)Math.Floor(focus + settings.RenderDistance) + 1);
        if (end <= first) return;

        float originY = height * (.5f + settings.Vertical / 100f + settings.TextY / 1080f);
        float horizontalOffset = width * settings.Horizontal / 100f;
        int active = Math.Clamp((int)Math.Round(focus), first, end - 1);
        // Draw the closest line last, above the cards and shadows of surrounding lines.
        for (int i = first; i <= end; i++)
        {
            if (i == active) continue;
            int index = i == end ? active : i;
            double distance = Math.Abs(focus - index);
            float y = originY + (float)((index - focus) * gap);
            var scaled = settings.DepthEffect
                ? settings.FarScale + (1 - settings.FarScale) / (1 + settings.ScaleFalloff * (Math.Pow(1 + distance, settings.ScaleCurve) - 1))
                : 1;
            var alpha = settings.DepthEffect ? (float)Math.Exp(-settings.FadeFalloff * (Math.Pow(1 + distance, settings.FadeCurve) - 1)) : 1;
            float font = (float)Math.Max(8, width * .05 * .87 * scaled * settings.FontScale / 100);
            // Skip off-screen rows before rasterizing. A title and translation fit within three font heights.
            float checkHeight = font * 1.5f + padding;
            if (y + checkHeight <= 0 || y - checkHeight >= height) continue;
            var line = frame.Lines[index];
            if (!settings.Translation && !line.IsMetadata) line = line with { Translation = null };
            var glyph = text.Get(new SceneText(line.Text, line.Translation, settings.FontFamily));
            if (glyph.InkSize == Vector2.Zero) continue;
            var inkSize = glyph.InkSize * (font / ISceneText.FontPixels);
            var half = inkSize / 2 + new Vector2(padding);
            if (y + half.Y <= 0 || y - half.Y >= height) continue;
            float x = settings.Alignment switch
            {
                LyricAlignment.Left => half.X + width * .075f,
                LyricAlignment.Right => width * .925f - half.X,
                _ => width / 2f
            };
            x += horizontalOffset;
            cards.Add(new(new(x, y), half, inkSize, glyph.Uv, settings.TextColor, alpha * settings.Opacity / 100f, padding));
        }
    }

    private static float RowGap(int height, WallpaperSettings settings) => height * (.135f + 83f / 1080) * settings.Spacing / 100;
}
