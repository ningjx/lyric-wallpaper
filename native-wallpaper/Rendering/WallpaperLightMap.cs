using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;

namespace LyricWallpaper.Rendering;

internal readonly record struct WallpaperLight(Vector2 Position, Vector3 Color, float Strength)
{
    public (Vector2 Direction, float Focus, float Weight) At(Vector2 center, float screenHeight)
    {
        var offset = Position - center;
        float distance = offset.Length();
        float falloff = distance / (screenHeight * .6f);
        return (distance > 0 ? offset / distance : Vector2.Zero, Math.Min(1, distance / (screenHeight * .04f)),
            Strength / (1 + falloff * falloff));
    }
}

internal sealed class WallpaperLightMap
{
    public const int MaxSources = 4;
    private const int ThumbnailSize = 128, Columns = 64, Rows = 36;
    private readonly Vector3[] pixels = new Vector3[ThumbnailSize * ThumbnailSize];

    public WallpaperLightMap(Bitmap bitmap)
    {
        using var thumbnail = new Bitmap(ThumbnailSize, ThumbnailSize, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(thumbnail))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(bitmap, new Rectangle(0, 0, ThumbnailSize, ThumbnailSize));
        }
        for (int row = 0; row < ThumbnailSize; row++)
        for (int column = 0; column < ThumbnailSize; column++)
        {
            var color = thumbnail.GetPixel(column, row);
            pixels[row * ThumbnailSize + column] = new Vector3(color.R, color.G, color.B) / 255;
        }
    }

    public WallpaperLight[] Find(Vector2 size, Vector2 uvScale, float zoom, Vector2 offset, bool tile)
    {
        Span<Vector3> colors = stackalloc Vector3[Columns * Rows];
        Span<float> light = stackalloc float[colors.Length];
        light.Clear();
        float sum = 0;
        int visible = 0;
        for (int row = 0; row < Rows; row++)
        for (int column = 0; column < Columns; column++)
        {
            var screen = new Vector2((column + .5f) / Columns, (row + .5f) / Rows);
            var uv = (screen - new Vector2(.5f) - offset) / zoom * uvScale + new Vector2(.5f);
            if (tile) uv -= new Vector2(MathF.Floor(uv.X), MathF.Floor(uv.Y));
            int index = row * Columns + column;
            if (uv.X is < 0 or > 1 || uv.Y is < 0 or > 1) continue;
            var color = colors[index] = Sample(uv);
            var linear = color * (color * (color * .305306f + new Vector3(.682171f)) + new Vector3(.012523f));
            float luminance = Vector3.Dot(linear, new(.2126f, .7152f, .0722f));
            sum += light[index] = .75f * luminance + .25f * MaxChannel(linear);
            visible++;
        }
        if (visible == 0) return [];
        float threshold = Math.Max(.16f, sum / visible + .07f);
        for (int index = 0; index < light.Length; index++)
        {
            float excess = Math.Max(0, light[index] - threshold);
            light[index] = excess * excess;
        }

        var regions = new List<WallpaperLight>();
        Span<(int Index, float Weight)> queue = stackalloc (int, float)[light.Length];
        for (int start = 0; start < light.Length; start++)
        {
            if (light[start] <= 0) continue;
            queue[0] = (start, light[start]);
            light[start] = 0;
            int count = 1;
            float total = 0, peak = 0;
            Vector2 position = default;
            Vector3 tint = default;
            for (int current = 0; current < count; current++)
            {
                var (index, weight) = queue[current];
                int row = index / Columns, column = index % Columns;
                total += weight;
                peak = Math.Max(peak, weight);
                position += new Vector2((column + .5f) / Columns, (row + .5f) / Rows) * weight;
                tint += colors[index] * weight;
                for (int neighborRow = Math.Max(0, row - 1); neighborRow <= Math.Min(Rows - 1, row + 1); neighborRow++)
                for (int neighborColumn = Math.Max(0, column - 1); neighborColumn <= Math.Min(Columns - 1, column + 1); neighborColumn++)
                {
                    int neighbor = neighborRow * Columns + neighborColumn;
                    if (light[neighbor] <= 0) continue;
                    queue[count++] = (neighbor, light[neighbor]);
                    light[neighbor] = 0;
                }
            }
            if (count < 3 || total < .035f) continue;
            float strength = MathF.Sqrt(peak) * MathF.Sqrt(Math.Min(total, peak * 48));
            regions.Add(new(position / total * size, Vector3.Lerp(Vector3.One, tint / MaxChannel(tint), .68f), strength));
        }
        if (regions.Count == 0) return [];
        regions.Sort(static (first, second) => second.Strength.CompareTo(first.Strength));
        float strongest = regions[0].Strength;
        return regions.Take(MaxSources).TakeWhile(region => region.Strength >= strongest * .12f)
            .Select(region => region with { Strength = region.Strength / strongest }).ToArray();
    }

    private Vector3 Sample(Vector2 uv)
    {
        var pixel = Vector2.Clamp(uv * ThumbnailSize - new Vector2(.5f), Vector2.Zero, new(ThumbnailSize - 1));
        int left = (int)pixel.X, top = (int)pixel.Y;
        int right = Math.Min(left + 1, ThumbnailSize - 1), bottom = Math.Min(top + 1, ThumbnailSize - 1);
        return Vector3.Lerp(
            Vector3.Lerp(pixels[top * ThumbnailSize + left], pixels[top * ThumbnailSize + right], pixel.X - left),
            Vector3.Lerp(pixels[bottom * ThumbnailSize + left], pixels[bottom * ThumbnailSize + right], pixel.X - left),
            pixel.Y - top);
    }

    private static float MaxChannel(Vector3 color) => Math.Max(color.X, Math.Max(color.Y, color.Z));
}
