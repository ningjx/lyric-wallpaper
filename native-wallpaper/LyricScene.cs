using System.Drawing.Text;
using System.Numerics;

internal sealed class LyricScene : IDisposable
{
    private static readonly string[] Lines =
    [
        "把城市的声音调低", "听见晚风穿过缝隙", "有一颗星落在眼底", "让时间慢慢流成光",
        "我们漂浮在夜色里", "等一场温柔的潮汐", "把没说完的话藏起", "交给遥远的天际",
        "当世界安静了一秒", "月光轻轻落在肩上", "沿着你的目光远行", "直到天色渐渐明亮"
    ];
    private static readonly double[] Times = [0, 5.5, 11, 16.5, 22.5, 28, 33.5, 39, 44.5, 50, 55.5, 61];
    // Must match the number of rows used by Glass.hlsl.
    private const int Slots = 16, AtlasFont = 128, CellWidth = 1408, CellHeight = 224;
    private const double Start = 16.5, Period = 67;
    private static readonly LyricLine[] DemoLines = Lines.Select((line, i) => new LyricLine(Times[i], line)).ToArray();
    private const double TestInterval = 3;
    private static readonly LyricLine[] TestLines = BuildTestLines();
    private readonly Entry[] atlas = new Entry[Slots];
    private readonly Bitmap atlasRow = new(CellWidth, CellHeight);
    private string fontFamily = "";
    private Font? titleFont, translationFont;
    private readonly StringFormat format = new(StringFormat.GenericTypographic)
    {
        Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center,
        FormatFlags = StringFormatFlags.NoWrap
    };
    private readonly Brush translationBrush = new SolidBrush(Color.FromArgb(190, Color.White));
    private long usage;
    private struct Entry { public string? Text, Translation; public float Width; public long LastUse; }
    public Size AtlasSize => new(CellWidth, CellHeight * Slots);

    public void InvalidateText() => Array.Clear(atlas);

    private void UseFont(string family)
    {
        if (fontFamily == family) return;
        titleFont?.Dispose();
        translationFont?.Dispose();
        using var selected = new Font(family, AtlasFont, FontStyle.Regular, GraphicsUnit.Pixel);
        var style = selected.FontFamily.IsStyleAvailable(FontStyle.Bold) ? FontStyle.Bold : FontStyle.Regular;
        titleFont = new Font(selected.FontFamily, AtlasFont, style, GraphicsUnit.Pixel);
        translationFont = new Font(selected.FontFamily, 58, FontStyle.Regular, GraphicsUnit.Pixel);
        fontFamily = family;
        InvalidateText();
    }

    private int TextSlot(D3D11Renderer renderer, LyricLine line)
    {
        int slot = 0;
        for (int i = 0; i < atlas.Length; i++)
        {
            if (atlas[i].Text == line.Text && atlas[i].Translation == line.Translation)
            {
                atlas[i].LastUse = ++usage;
                return i;
            }
            if (atlas[i].LastUse < atlas[slot].LastUse) slot = i;
        }

        bool twoLines = !string.IsNullOrWhiteSpace(line.Translation);
        float width;
        using (var graphics = Graphics.FromImage(atlasRow))
        {
            graphics.Clear(Color.Transparent);
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            width = DrawText(graphics, line.Text, titleFont!, Brushes.White, twoLines ? 76 : CellHeight / 2f);
            if (twoLines)
                width = Math.Max(width, DrawText(graphics, line.Translation!, translationFont!, translationBrush, 172));
        }
        renderer.UpdateTextRow(slot, atlasRow);
        atlas[slot] = new Entry { Text = line.Text, Translation = line.Translation, Width = width, LastUse = ++usage };
        return slot;
    }

    private float DrawText(Graphics graphics, string text, Font font, Brush brush, float center)
    {
        float measure = graphics.MeasureString(text, font, PointF.Empty, format).Width;
        float scale = Math.Min(1, (CellWidth - 12) / Math.Max(1, measure));
        // Long lines remain fully visible, without allocating a larger texture.
        graphics.TranslateTransform(CellWidth / 2f, center);
        graphics.ScaleTransform(scale, scale);
        graphics.DrawString(text, font, brush, PointF.Empty, format);
        graphics.ResetTransform();
        return Math.Max(1, measure * scale);
    }

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

    public uint Build(Span<Vector4> scene, D3D11Renderer renderer, LyricFrame frame, WallpaperSettings settings)
    {
        UseFont(settings.FontFamily);
        scene[33] = Rgb(settings.TextColor, settings.Glass / 100f);
        scene[35] = new(settings.RefractionHeight, -settings.RefractionAmount, settings.Blur, settings.CornerRadius);
        scene[36] = new((float)settings.Saturation, (float)settings.Brightness, (float)settings.Contrast, settings.Chromatic ? 1 : 0);
        scene[37] = Rgb(settings.HighlightColor, settings.Highlight ? (float)settings.HighlightAlpha : 0);
        scene[38] = new((float)settings.HighlightAngle, (float)settings.HighlightFalloff, (float)settings.HighlightWidth, (int)settings.HighlightMode);
        scene[39] = Rgb(settings.TintColor, (float)settings.TintAlpha);
        scene[40] = Rgb(settings.SurfaceColor, (float)settings.SurfaceAlpha);
        scene[41] = Rgb(settings.ShadowColor, settings.Shadow ? (float)settings.ShadowAlpha : 0);
        scene[42] = new(settings.ShadowRadius, settings.ShadowX, settings.ShadowY, settings.BehindGlass ? 1 : 0);
        scene[43] = new((float)settings.FarGlass, settings.DepthEffect ? 1 : 0, settings.TextY, 0);
        int width = renderer.Width, height = renderer.Height;
        double focus = frame.Focus;
        float gap = RowGap(height, settings);
        float padding = settings.Padding * height / 1080f;
        int count = 0;
        int first = Math.Max(0, (int)Math.Ceiling(focus - settings.RenderDistance));
        int end = Math.Min(frame.Lines.Length, (int)Math.Floor(focus + settings.RenderDistance) + 1);
        if (end <= first) return 0;
        int active = Math.Clamp((int)Math.Round(focus), first, end - 1);
        // Draw the closest line last, above the cards and shadows of surrounding lines.
        for (int i = first; i <= end; i++)
        {
            if (i == active) continue;
            int index = i == end ? active : i;
            double distance = Math.Abs(focus - index);
            float y = (float)(height * (.5 + settings.Vertical / 100.0) + (index - focus) * gap);
            var scaled = settings.DepthEffect
                ? settings.FarScale + (1 - settings.FarScale) / (1 + settings.ScaleFalloff * (Math.Pow(1 + distance, settings.ScaleCurve) - 1))
                : 1;
            var alpha = settings.DepthEffect ? (float)Math.Exp(-settings.FadeFalloff * (Math.Pow(1 + distance, settings.FadeCurve) - 1)) : 1;
            var line = frame.Lines[index];
            if (!settings.Translation && !line.IsMetadata) line = line with { Translation = null };
            float font = (float)Math.Max(8, width * .05 * .87 * scaled * settings.FontScale / 100);
            float textHeight = font * CellHeight / AtlasFont;
            bool twoLines = !string.IsNullOrWhiteSpace(line.Translation);
            float cardHeight = font * (twoLines ? 1.58f : 1.155625f) + 2 * padding;
            if (y + cardHeight / 2 <= 0 || y - cardHeight / 2 >= height) continue;
            int slot = TextSlot(renderer, line);
            float halfWidth = atlas[slot].Width * font / AtlasFont / 2 + padding;
            float x = settings.Alignment switch
            {
                LyricAlignment.Left => halfWidth + width * .075f,
                LyricAlignment.Right => width * .925f - halfWidth,
                _ => width / 2f
            };
            x += width * settings.Horizontal / 100f;
            scene[1 + count] = new(x, y, halfWidth, cardHeight / 2);
            scene[17 + count] = new(CellWidth * font / AtlasFont, textHeight, alpha * settings.Opacity / 100f, slot);
            count++;
        }
        return (uint)count;
    }

    private static Vector4 Rgb(int argb, float alpha)
    {
        Color color = Color.FromArgb(argb);
        return new(color.R / 255f, color.G / 255f, color.B / 255f, alpha);
    }

    private static float RowGap(int height, WallpaperSettings settings) => height * (.135f + 83f / 1080) * settings.Spacing / 100;
    public void Dispose()
    {
        atlasRow.Dispose();
        titleFont?.Dispose();
        translationFont?.Dispose();
        format.Dispose();
        translationBrush.Dispose();
    }
}
