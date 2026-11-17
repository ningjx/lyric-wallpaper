using System.Drawing.Imaging;
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
    private const int Slots = 16, AtlasFont = 128, CellWidth = 4096, CellHeight = 288;
    private const int GlyphBorder = 2;
    private const double Start = 16.5, Period = 67;
    private static readonly LyricLine[] DemoLines = Lines.Select((line, i) => new LyricLine(Times[i], line)).ToArray();
    private const double TestInterval = 3;
    private static readonly LyricLine[] TestLines = BuildTestLines();
    private readonly Entry[] atlas = new Entry[Slots];
    private readonly Bitmap atlasRow = new(CellWidth, CellHeight, PixelFormat.Format32bppArgb);
    private readonly byte[] atlasMask = new byte[CellWidth * CellHeight];
    private string fontFamily = "";
    private Font? titleFont, translationFont;
    private readonly StringFormat format = new(StringFormat.GenericTypographic)
    {
        Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near,
        FormatFlags = StringFormatFlags.NoWrap,
        Trimming = StringTrimming.None
    };
    private readonly Brush translationBrush = new SolidBrush(Color.FromArgb(190, Color.White));
    private long usage;
    private struct Entry
    {
        public string? Text, Translation;
        public Vector2 InkSize;
        public Vector4 Uv;
        public long LastUse;
    }
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

        float rasterScale;
        using (var graphics = Graphics.FromImage(atlasRow))
        {
            graphics.Clear(Color.Transparent);
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var titleSize = graphics.MeasureString(line.Text, titleFont!, PointF.Empty, format);
            bool hasTranslation = !string.IsNullOrWhiteSpace(line.Translation);
            var translationSize = hasTranslation
                ? graphics.MeasureString(line.Translation!, translationFont!, PointF.Empty, format)
                : SizeF.Empty;
            float translationTop = titleSize.Height + (hasTranslation ? 8 : 0);
            float width = Math.Max(titleSize.Width, translationSize.Width);
            float height = translationTop + translationSize.Height;
            // Raster resolution can fall back for large blocks. Applying the inverse in Build keeps the font size on screen.
            rasterScale = Math.Min(1, Math.Min((CellWidth - GlyphBorder * 2) / Math.Max(1, width),
                (CellHeight - GlyphBorder * 2) / Math.Max(1, height)));
            graphics.TranslateTransform(CellWidth / 2f, GlyphBorder);
            graphics.ScaleTransform(rasterScale, rasterScale);
            graphics.DrawString(line.Text, titleFont!, Brushes.White, PointF.Empty, format);
            if (hasTranslation)
                graphics.DrawString(line.Translation!, translationFont!, translationBrush,
                    new PointF(0, translationTop), format);
        }
        var ink = CopyAtlasMask();
        renderer.UpdateTextRow(slot, atlasRow.Size, atlasMask);
        atlas[slot] = new Entry
        {
            Text = line.Text, Translation = line.Translation, LastUse = ++usage,
            InkSize = new Vector2(ink.Width, ink.Height) / rasterScale,
            Uv = new Vector4(ink.Left / (float)CellWidth, (slot * CellHeight + ink.Top) / (float)AtlasSize.Height,
                ink.Width / (float)CellWidth, ink.Height / (float)AtlasSize.Height)
        };
        return slot;
    }

    private unsafe Rectangle CopyAtlasMask()
    {
        int left = CellWidth, right = 0, top = CellHeight, bottom = 0;
        var bounds = new Rectangle(Point.Empty, atlasRow.Size);
        var pixels = atlasRow.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < CellHeight; y++)
            {
                byte* source = (byte*)pixels.Scan0 + y * pixels.Stride;
                var mask = atlasMask.AsSpan(y * CellWidth, CellWidth);
                for (int x = 0; x < CellWidth; x++) mask[x] = source[x * 4 + 3];
                int first = mask.IndexOfAnyExcept((byte)0);
                if (first < 0) continue;
                left = Math.Min(left, first);
                right = Math.Max(right, mask.LastIndexOfAnyExcept((byte)0) + 1);
                top = Math.Min(top, y);
                bottom = y + 1;
            }
        }
        finally { atlasRow.UnlockBits(pixels); }
        return bottom > top ? Rectangle.FromLTRB(left, top, right, bottom) : new Rectangle(0, 0, 1, 1);
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
        int width = renderer.Width, height = renderer.Height;
        double focus = frame.Focus;
        float gap = RowGap(height, settings);
        float padding = settings.Padding * height / 1080f;
        scene[43] = new((float)settings.FarGlass, settings.DepthEffect ? 1 : 0, padding, 0);
        int first = Math.Max(0, (int)Math.Ceiling(focus - settings.RenderDistance));
        int end = Math.Min(frame.Lines.Length, (int)Math.Floor(focus + settings.RenderDistance) + 1);
        if (end <= first) return 0;

        float originY = height * (.5f + settings.Vertical / 100f + settings.TextY / 1080f);
        int active = Math.Clamp((int)Math.Round(focus), first, end - 1);
        int count = 0;
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
            int slot = TextSlot(renderer, line);
            var entry = atlas[slot];
            var inkSize = entry.InkSize * (font / AtlasFont);
            var half = inkSize / 2 + new Vector2(padding);
            if (y + half.Y <= 0 || y - half.Y >= height) continue;
            float x = settings.Alignment switch
            {
                LyricAlignment.Left => half.X + width * .075f,
                LyricAlignment.Right => width * .925f - half.X,
                _ => width / 2f
            };
            x += width * settings.Horizontal / 100f;
            scene[1 + count] = new(x, y, half.X, half.Y);
            scene[17 + count] = new(inkSize, alpha * settings.Opacity / 100f, 0);
            scene[45 + count] = entry.Uv;
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
