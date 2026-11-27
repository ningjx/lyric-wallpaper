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

    public static double LeadSeconds(int width, int height, WallpaperSettings settings)
    {
        // Approximate the distance between centers using a two-line card and the edge gap.
        double step = EdgeGap(height, settings) + 2 * (Math.Max(8, NominalFontSize(width, settings)) + Padding(height, settings));
        double speed = settings.ScrollSpeed;
        double normalized = 0;
        while (normalized < 12 && (step * (1 + normalized) * Math.Exp(-normalized) > .75 ||
                                   step * speed * normalized * Math.Exp(-normalized) > 3))
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
        // The middle copy plays on a continuous time axis, with neighboring rows across the loop.
        return Enumerable.Range(0, Lines.Length * 3).Select(i =>
        {
            int line = i % Lines.Length;
            return new LyricLine(i * TestInterval, line == 3 ? "让时间慢慢流成光 · 测试滚动" : Lines[line], captions[line]);
        }).ToArray();
    }

    public static LyricFrame TestAt(double elapsed, double lead, WallpaperSettings settings)
    {
        double period = Lines.Length * TestInterval;
        double phase = (elapsed + TestInterval * 3 + settings.OffsetMs / 1000d) % period;
        return LyricTimeline.PlayingAt(TestLines, period + phase, elapsed, lead, settings.ScrollSpeed);
    }

    public static TextColorSample? ColorSample(LyricFrame frame, bool automatic, GlassCard? center,
        TextColorSample? previous)
    {
        if (!automatic || center is not { } card) return null;
        // Keep the settled center sample while the next line scrolls in.
        return frame.Moving && previous is { } sample ? sample : new TextColorSample(card);
    }

    public static GlassCard? Collect(LyricFrame frame, WallpaperSettings settings, SceneViewport viewport, ISceneText text,
        List<GlassCard> cards)
    {
        int width = viewport.Width, height = viewport.Height;
        double focus = frame.Focus;
        int first = Math.Max(0, (int)Math.Ceiling(focus - settings.RenderDistance));
        int end = Math.Min(frame.Lines.Length, (int)Math.Floor(focus + settings.RenderDistance) + 1);
        if (end <= first) return null;

        float gap = EdgeGap(height, settings);
        float padding = Padding(height, settings);
        double fontSize = NominalFontSize(width, settings);
        float originY = height * (.5f + settings.Vertical / 100f + settings.TextY / 1080f);
        float horizontalOffset = width * settings.Horizontal / 100f;
        int active = Math.Clamp((int)Math.Round(focus), first, end - 1);
        int anchor = Math.Clamp((int)Math.Floor(focus), first, end - 1);
        var anchorCard = Measure(anchor);
        float fraction = anchor + 1 < end ? (float)Math.Clamp(focus - anchor, 0, 1) : 0;
        var nextCard = fraction > 0 ? Measure(anchor + 1) : default;
        // Keep the interpolated focus at the same screen position as both cards change height.
        float anchorY = originY - fraction * (anchorCard.Half.Y + gap + nextCard.Half.Y);
        GlassCard? foreground = null;
        int firstCard = cards.Count;
        Place(anchor, anchorCard, anchorY);

        float top = anchorY - anchorCard.Half.Y;
        for (int i = anchor - 1; i >= first && top > gap; i--)
        {
            float bottom = top - gap;
            var card = Measure(i);
            Place(i, card, bottom - card.Half.Y);
            top = bottom - 2 * card.Half.Y;
        }
        cards.Reverse(firstCard, cards.Count - firstCard);

        float lowerEdge = anchorY + anchorCard.Half.Y;
        for (int i = anchor + 1; i < end && lowerEdge + gap < height; i++)
        {
            float nextTop = lowerEdge + gap;
            var card = i == anchor + 1 && fraction > 0 ? nextCard : Measure(i);
            Place(i, card, nextTop + card.Half.Y);
            lowerEdge = nextTop + 2 * card.Half.Y;
        }
        // Draw the closest line last, above any surrounding shadows.
        if (foreground is { } closest)
        {
            cards.Add(closest);
            return closest;
        }
        // The center can be clipped when the layout is shifted toward the edge of the screen.
        for (int i = firstCard; i < cards.Count; i++)
        {
            if (foreground is not { } nearest || MathF.Abs(cards[i].Center.Y - originY) < MathF.Abs(nearest.Center.Y - originY))
                foreground = cards[i];
        }
        return foreground;

        GlassCard Measure(int index)
        {
            double distance = Math.Abs(focus - index);
            var scaled = settings.DepthEffect
                ? settings.FarScale + (1 - settings.FarScale) / (1 + settings.ScaleFalloff * (Math.Pow(1 + distance, settings.ScaleCurve) - 1))
                : 1;
            var alpha = settings.DepthEffect ? (float)Math.Exp(-settings.FadeFalloff * (Math.Pow(1 + distance, settings.FadeCurve) - 1)) : 1;
            float font = (float)Math.Max(8, fontSize * scaled);
            var line = frame.Lines[index];
            var translation = settings.Translation || line.IsMetadata ? line.Translation : null;
            var glyph = text.Get(new SceneText(line.Text, translation, settings.FontFamily));
            if (glyph.InkSize == Vector2.Zero) return default;
            var inkSize = glyph.InkSize * (font / ISceneText.FontPixels);
            var half = inkSize / 2 + new Vector2(padding);
            float x = settings.Alignment switch
            {
                LyricAlignment.Left => half.X + width * .075f,
                LyricAlignment.Right => width * .925f - half.X,
                _ => width / 2f
            };
            return new(new(x + horizontalOffset, 0), half, inkSize, glyph.Uv, settings.TextColor, alpha * settings.Opacity / 100f, padding);
        }

        void Place(int index, GlassCard card, float y)
        {
            if (card.Ink == Vector2.Zero || y + card.Half.Y <= 0 || y - card.Half.Y >= height) return;
            card = card with { Center = new(card.Center.X, y) };
            if (index == active) foreground = card;
            else cards.Add(card);
        }
    }

    private static float EdgeGap(int height, WallpaperSettings settings) => height * (83f / 1080) * settings.Spacing / 100;
    private static float Padding(int height, WallpaperSettings settings) => settings.Padding * height / 1080f;
    private static double NominalFontSize(int width, WallpaperSettings settings) => width * .05 * .87 * settings.FontScale / 100;
}
