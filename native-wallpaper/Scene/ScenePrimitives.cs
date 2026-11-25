using System.Numerics;

namespace LyricWallpaper.Scene;

internal readonly record struct SceneText(string Text, string? Secondary, string FontFamily);
internal readonly record struct TextGlyph(Vector2 InkSize, Vector4 Uv);

internal interface ISceneText
{
    const int FontPixels = 128;
    TextGlyph Get(SceneText text);
}

internal readonly record struct SceneViewport(int Width, int Height, Rectangle Output)
{
    public RectangleF ToOutput(Vector2 center, Vector2 half)
    {
        float sx = Output.Width / (float)Width, sy = Output.Height / (float)Height;
        return new(Output.X + (center.X - half.X) * sx, Output.Y + (center.Y - half.Y) * sy,
            2 * half.X * sx, 2 * half.Y * sy);
    }
}

internal readonly record struct GlassCard(Vector2 Center, Vector2 Half, Vector2 Ink, Vector4 Uv, int Color,
    float Opacity, float Padding, float Press = 0, bool AutoInvertText = false);

internal readonly record struct TextColorSample(Vector2 Center, Vector2 Half, Vector2 Ink, float Opacity)
{
    public TextColorSample(GlassCard card) : this(card.Center, card.Half, card.Ink, card.Opacity) { }
}

internal readonly record struct WidgetPreviewFrame(string Type, int Width, int Height, byte[] Pixels);

internal sealed record WidgetEditorBackdrop(int Width, int Height, byte[] Pixels);

internal readonly record struct WidgetLayout(Guid Id, RectangleF Bounds, int Radius)
{
    public bool Contains(Point point)
    {
        if (!Bounds.Contains(point)) return false;
        double x = Math.Max(0, Math.Abs(point.X - Bounds.Left - Bounds.Width / 2) - (Bounds.Width / 2 - Radius));
        double y = Math.Max(0, Math.Abs(point.Y - Bounds.Top - Bounds.Height / 2) - (Bounds.Height / 2 - Radius));
        return x * x + y * y <= Radius * Radius;
    }
}
