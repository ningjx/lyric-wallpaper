using System.Globalization;
using System.Numerics;
using LyricWallpaper.Rendering;
using W = System.Windows;
using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;

namespace LyricWallpaper.Presentation;

internal sealed class SettingsLabelAtlas
{
    private static readonly Media.Typeface TextFont = new(SettingsTheme.FontFamily, W.FontStyles.Normal,
        W.FontWeights.Medium, W.FontStretches.Normal);
    private static readonly Media.Typeface IconFont = new("Segoe Fluent Icons");
    private static readonly Media.Brush Ink = MakeInk(32, 46, 63);
    private static readonly Media.Brush SelectedInk = MakeInk(0, 84, 163);
    private readonly Dictionary<Style, Tile> tiles = [];
    private int width, height, padding;
    private float dpi;

    public bool Layout(D3D11Renderer.SettingsSurface renderer, List<GlassLabel> labels, float scale,
        List<SettingsLabel> placements)
    {
        placements.Clear();
        if (dpi != scale)
        {
            tiles.Clear();
            dpi = scale;
            padding = Math.Max(2, (int)Math.Ceiling(2 * dpi));
        }
        if (labels.Count == 0) return false;
        bool uploaded = false;
        foreach (var label in labels)
        {
            if (tiles.ContainsKey(Style.From(label))) continue;
            Upload(renderer, labels);
            uploaded = true;
            break;
        }
        foreach (var label in labels)
        {
            var tile = tiles[Style.From(label)];
            var box = label.Bounds;
            var clip = label.Clip;
            float halfWidth = tile.Width * .5f, halfHeight = tile.Height * .5f;
            double left = label.Icon is null
                ? (box.Left + box.Width * .5) * dpi - tile.TextWidth * .5
                : (box.Left + 14) * dpi;
            float x = (float)Math.Round(left - padding);
            float y = (float)Math.Round((box.Top + box.Height * .5) * dpi - halfHeight);
            placements.Add(new(new(x + halfWidth, y + halfHeight, halfWidth, halfHeight),
                new Vector4((float)clip.Left, (float)clip.Top, (float)clip.Right, (float)clip.Bottom) * dpi,
                new(0, tile.Top, 1f / width, 1f / height)));
        }
        return uploaded;
    }

    private void Upload(D3D11Renderer.SettingsSurface renderer, List<GlassLabel> labels)
    {
        // Keep label pixels independent of scroll position. The GPU positions and clips each tile.
        tiles.Clear();
        width = height = 0;
        var visual = new Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            foreach (var label in labels)
            {
                var style = Style.From(label);
                if (tiles.ContainsKey(style)) continue;
                var brush = style.Selected ? SelectedInk : Ink;
                var text = new Media.FormattedText(style.Text, CultureInfo.CurrentUICulture, W.FlowDirection.LeftToRight,
                    TextFont, style.Size * dpi, brush, 1);
                var icon = style.Icon is { } symbol
                    ? new Media.FormattedText(symbol, CultureInfo.InvariantCulture, W.FlowDirection.LeftToRight, IconFont, 16 * dpi, brush, 1)
                    : null;
                double offset = icon is null ? 0 : 26 * dpi;
                int tileWidth = (int)Math.Ceiling(Math.Max(text.Width + offset, icon?.Width ?? 0)) + padding * 2;
                int tileHeight = (int)Math.Ceiling(Math.Max(text.Height, icon?.Height ?? 0)) + padding * 2;
                double centerY = height + tileHeight * .5;
                if (icon is not null) dc.DrawText(icon, new W.Point(padding, centerY - icon.Height * .5));
                dc.DrawText(text, new W.Point(padding + offset, centerY - text.Height * .5));
                tiles.Add(style, new(height, tileWidth, tileHeight, text.Width));
                width = Math.Max(width, tileWidth);
                height += tileHeight;
            }
        }
        var bitmap = new Imaging.RenderTargetBitmap(width, height, 96, 96, Media.PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[checked(width * height * 4)];
        bitmap.CopyPixels(pixels, width * 4, 0);
        renderer.SetText(width, height, pixels);
    }

    private static Media.Brush MakeInk(byte r, byte g, byte b)
    {
        var brush = new Media.SolidColorBrush(Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private readonly record struct Style(string Text, string? Icon, double Size, bool Selected)
    {
        public static Style From(GlassLabel label) => new(label.Text, label.Icon, label.Size, label.IsSelected);
    }

    private readonly record struct Tile(int Top, int Width, int Height, double TextWidth);
}
