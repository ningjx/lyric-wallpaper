using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;

namespace LyricWallpaper.Rendering;

internal readonly record struct WallpaperLight(Vector2 Position, Vector3 Color)
{
    public (float Angle, float Focus) At(Vector2 center, float screenHeight)
    {
        var offset = Position - center;
        return (MathF.Atan2(offset.Y, offset.X), Math.Clamp(offset.Length() / (screenHeight * .04f), 0, 1));
    }
}

internal sealed class WallpaperLightMap
{
    private const int ThumbnailSize = 128, Columns = 64, Rows = 36, Radius = 3;
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

    public WallpaperLight? Find(Vector2 size, Vector2 uvScale, float zoom, Vector2 offset, bool tile)
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
        if (visible == 0) return null;
        float threshold = Math.Max(.16f, sum / visible + .07f);
        for (int index = 0; index < light.Length; index++)
        {
            float excess = Math.Max(0, light[index] - threshold);
            light[index] = excess * excess;
        }

        float best = .02f;
        int bestRow = -1, bestColumn = -1;
        for (int row = 0; row < Rows; row++)
        for (int column = 0; column < Columns; column++)
        {
            if (light[row * Columns + column] == 0) continue;
            float score = 0;
            for (int sampleRow = Math.Max(0, row - Radius); sampleRow <= Math.Min(Rows - 1, row + Radius); sampleRow++)
            for (int sampleColumn = Math.Max(0, column - Radius); sampleColumn <= Math.Min(Columns - 1, column + Radius); sampleColumn++)
                score += light[sampleRow * Columns + sampleColumn] * Weight(sampleColumn - column, sampleRow - row);
            if (score <= best) continue;
            (best, bestRow, bestColumn) = (score, row, column);
        }
        if (bestRow < 0) return null;

        float total = 0;
        Vector2 position = default;
        Vector3 tint = default;
        for (int row = Math.Max(0, bestRow - Radius); row <= Math.Min(Rows - 1, bestRow + Radius); row++)
        for (int column = Math.Max(0, bestColumn - Radius); column <= Math.Min(Columns - 1, bestColumn + Radius); column++)
        {
            int index = row * Columns + column;
            float weight = light[index] * Weight(column - bestColumn, row - bestRow);
            if (weight <= 0) continue;
            total += weight;
            position += new Vector2((column + .5f) / Columns, (row + .5f) / Rows) * weight;
            tint += colors[index] * weight;
        }
        return new(position / total * size, Vector3.Lerp(Vector3.One, tint / MaxChannel(tint), .68f));
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

    private static float Weight(int horizontal, int vertical) =>
        1f / (1f + .24f * (horizontal * horizontal + vertical * vertical));

    private static float MaxChannel(Vector3 color) => Math.Max(color.X, Math.Max(color.Y, color.Z));
}
