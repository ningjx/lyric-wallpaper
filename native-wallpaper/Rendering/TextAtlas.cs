using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Numerics;
using LyricWallpaper.Scene;

namespace LyricWallpaper.Rendering;

internal sealed class TextAtlas : ISceneText, IDisposable
{
    private const int FontPixels = ISceneText.FontPixels;
    private const int Slots = 20, CellWidth = 4096, CellHeight = 288, Border = 2;
    private readonly Entry[] entries = new Entry[Slots];
    private readonly Bitmap row = new(CellWidth, CellHeight, PixelFormat.Format32bppArgb);
    private readonly byte[] mask = new byte[CellWidth * CellHeight];
    private readonly Dictionary<string, (Font Main, Font Secondary)> fonts = new(StringComparer.Ordinal);
    private readonly StringFormat format = new(StringFormat.GenericTypographic)
    {
        Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near,
        FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.None
    };
    private readonly Brush secondaryBrush = new SolidBrush(Color.FromArgb(190, Color.White));
    private D3D11Renderer? target;
    private long frame, usage;
    private record struct Entry(SceneText Text, TextGlyph Glyph, long Frame, long LastUse);

    public static Size Size => new(CellWidth, CellHeight * Slots);

    public void Attach(D3D11Renderer renderer)
    {
        target = renderer;
        Array.Clear(entries);
    }

    public void BeginFrame() => frame++;

    public TextGlyph Get(SceneText text)
    {
        int slot = -1;
        for (int i = 0; i < entries.Length; i++)
        {
            ref var entry = ref entries[i];
            if (entry.LastUse != 0 && entry.Text == text)
            {
                entry.Frame = frame;
                entry.LastUse = ++usage;
                return entry.Glyph;
            }
            if (entry.Frame != frame && (slot < 0 || entry.LastUse < entries[slot].LastUse)) slot = i;
        }
        // Slots referenced by this frame stay valid until Present.
        if (slot < 0) return default;
        var glyph = Rasterize(text, slot);
        entries[slot] = new(text, glyph, frame, ++usage);
        return glyph;
    }

    private TextGlyph Rasterize(SceneText text, int slot)
    {
        if (!fonts.TryGetValue(text.FontFamily, out var pair))
        {
            using var selected = new Font(text.FontFamily, FontPixels, FontStyle.Regular, GraphicsUnit.Pixel);
            var style = selected.FontFamily.IsStyleAvailable(FontStyle.Bold) ? FontStyle.Bold : FontStyle.Regular;
            pair = (new Font(selected.FontFamily, FontPixels, style, GraphicsUnit.Pixel),
                new Font(selected.FontFamily, 58, FontStyle.Regular, GraphicsUnit.Pixel));
            fonts.Add(text.FontFamily, pair);
        }

        float rasterScale;
        using (var graphics = Graphics.FromImage(row))
        {
            graphics.Clear(Color.Transparent);
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var main = graphics.MeasureString(text.Text, pair.Main, PointF.Empty, format);
            bool hasSecondary = !string.IsNullOrWhiteSpace(text.Secondary);
            var secondary = hasSecondary
                ? graphics.MeasureString(text.Secondary!, pair.Secondary, PointF.Empty, format) : SizeF.Empty;
            float secondaryTop = main.Height + (hasSecondary ? 8 : 0);
            float width = Math.Max(main.Width, secondary.Width), height = secondaryTop + secondary.Height;
            rasterScale = Math.Min(1, Math.Min((CellWidth - Border * 2) / Math.Max(1, width),
                (CellHeight - Border * 2) / Math.Max(1, height)));
            graphics.TranslateTransform(CellWidth / 2f, Border);
            graphics.ScaleTransform(rasterScale, rasterScale);
            graphics.DrawString(text.Text, pair.Main, Brushes.White, PointF.Empty, format);
            if (hasSecondary)
                graphics.DrawString(text.Secondary!, pair.Secondary, secondaryBrush, new PointF(0, secondaryTop), format);
        }

        var ink = CopyMask();
        target!.UpdateTextRow(slot, row.Size, mask);
        return new(new Vector2(ink.Width, ink.Height) / rasterScale,
            new(ink.Left / (float)CellWidth, (slot * CellHeight + ink.Top) / (float)Size.Height,
                ink.Width / (float)CellWidth, ink.Height / (float)Size.Height));
    }

    private unsafe Rectangle CopyMask()
    {
        int left = CellWidth, right = 0, top = CellHeight, bottom = 0;
        var pixels = row.LockBits(new Rectangle(Point.Empty, row.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < CellHeight; y++)
            {
                byte* source = (byte*)pixels.Scan0 + y * pixels.Stride;
                var line = mask.AsSpan(y * CellWidth, CellWidth);
                for (int x = 0; x < CellWidth; x++) line[x] = source[x * 4 + 3];
                int first = line.IndexOfAnyExcept((byte)0);
                if (first < 0) continue;
                left = Math.Min(left, first);
                right = Math.Max(right, line.LastIndexOfAnyExcept((byte)0) + 1);
                top = Math.Min(top, y);
                bottom = y + 1;
            }
        }
        finally { row.UnlockBits(pixels); }
        return bottom > top ? Rectangle.FromLTRB(left, top, right, bottom) : new Rectangle(0, 0, 1, 1);
    }

    public void Dispose()
    {
        row.Dispose();
        foreach (var pair in fonts.Values)
        {
            pair.Main.Dispose();
            pair.Secondary.Dispose();
        }
        format.Dispose();
        secondaryBrush.Dispose();
    }
}
