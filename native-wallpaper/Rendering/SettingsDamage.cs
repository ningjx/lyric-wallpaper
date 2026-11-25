using System.Numerics;

namespace LyricWallpaper.Rendering;

internal static class SettingsDamage
{
    public static Rectangle Bounds(SettingsPrimitive shape, Rectangle viewport) =>
        Bounds(shape.Bounds, shape.Clip, new(shape.Shape.X, shape.Shape.Y), viewport);

    public static Rectangle Bounds(SettingsLabel label, Rectangle viewport) =>
        Bounds(label.Bounds, label.Clip, Vector2.One, viewport);

    private static Rectangle Bounds(Vector4 geometry, Vector4 clip, Vector2 scale, Rectangle viewport)
    {
        var center = new Vector2(geometry.X, geometry.Y);
        // Match the two-pixel margin in ShapeVertex, including pixels used for antialiasing.
        var half = new Vector2(geometry.Z, geometry.W) * scale + new Vector2(2);
        return Pixels(Vector2.Max(center - half, new(clip.X, clip.Y)),
            Vector2.Min(center + half, new(clip.Z, clip.W)), viewport);
    }

    public static Rectangle Output(Rectangle baseDirty, IReadOnlyList<SettingsPrimitive> previous,
        IReadOnlyList<SettingsPrimitive> next, Rectangle viewport)
    {
        Rectangle dirty = Union(baseDirty, Changed(previous, next, viewport, Bounds));
        if (baseDirty.IsEmpty) return dirty;
        foreach (var lens in next)
        {
            var bounds = Bounds(lens, viewport);
            if (!dirty.Contains(bounds) && SampleBounds(lens, bounds, viewport).IntersectsWith(baseDirty))
                dirty = Union(dirty, bounds);
        }
        return dirty;
    }

    private static Rectangle SampleBounds(SettingsPrimitive lens, Rectangle pixels, Rectangle viewport)
    {
        if (pixels.IsEmpty || lens.Sampling.Z is 1 or 3) return pixels;
        var center = new Vector2(lens.Bounds.X, lens.Bounds.Y);
        var scale = new Vector2(lens.Shape.X, lens.Shape.Y);
        var first = new Vector2(pixels.Left, pixels.Top);
        var end = new Vector2(pixels.Right, pixels.Bottom);
        var offset = scale * MathF.Abs(lens.Optics.Z);
        if (lens.Shape.W > 0)
        {
            var local = Vector2.Max(Vector2.Abs(first - center), Vector2.Abs(end - center)) / scale;
            offset *= 1 + local.X * local.Y / MathF.Max(1, lens.Bounds.Z * lens.Bounds.W);
        }
        first -= offset;
        end += offset;
        if (lens.Sampling.Z == 2)
        {
            var sampling = new Vector2(lens.Sampling.X, lens.Sampling.Y);
            first = center + (first - center) * sampling;
            end = center + (end - center) * sampling;
        }
        // The lens also reads the undistorted pixel. Including it covers clamped edge samples.
        var margin = new Vector2(MathF.Max(0, lens.Optics.W) + 1);
        return Pixels(Vector2.Min(new(pixels.Left, pixels.Top), Vector2.Min(first, end) - margin),
            Vector2.Max(new(pixels.Right, pixels.Bottom), Vector2.Max(first, end) + margin), viewport);
    }

    public static Rectangle Changed<T>(IReadOnlyList<T> previous, IReadOnlyList<T> next, Rectangle viewport,
        Func<T, Rectangle, Rectangle> bounds, bool all = false) where T : struct, IEquatable<T>
    {
        Rectangle dirty = Rectangle.Empty;
        int common = Math.Min(previous.Count, next.Count);
        for (int i = 0; i < common; i++)
        {
            if (all || !previous[i].Equals(next[i]))
                dirty = Union(Union(dirty, bounds(previous[i], viewport)), bounds(next[i], viewport));
        }
        for (int i = common; i < previous.Count; i++) dirty = Union(dirty, bounds(previous[i], viewport));
        for (int i = common; i < next.Count; i++) dirty = Union(dirty, bounds(next[i], viewport));
        return dirty;
    }

    public static Rectangle Union(Rectangle first, Rectangle second) =>
        first.IsEmpty ? second : second.IsEmpty ? first : Rectangle.Union(first, second);

    private static Rectangle Pixels(Vector2 first, Vector2 end, Rectangle viewport)
    {
        if (first.X >= end.X || first.Y >= end.Y) return Rectangle.Empty;
        int left = Math.Max((int)MathF.Floor(first.X), viewport.Left);
        int top = Math.Max((int)MathF.Floor(first.Y), viewport.Top);
        int right = Math.Min((int)MathF.Ceiling(end.X), viewport.Right);
        int bottom = Math.Min((int)MathF.Ceiling(end.Y), viewport.Bottom);
        return left < right && top < bottom ? Rectangle.FromLTRB(left, top, right, bottom) : Rectangle.Empty;
    }
}
