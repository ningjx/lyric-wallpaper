using System.Drawing.Imaging;

namespace LyricWallpaper.Wallpaper;

internal static class BackgroundImages
{
    public static Bitmap Load(WallpaperSettings settings, string? selected = null)
    {
        string path = selected ?? settings.Background;
        if (path.Length == 0)
        {
            var solid = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
            solid.SetPixel(0, 0, Color.FromArgb(9, 14, 19));
            return solid;
        }
        using var image = Image.FromFile(path);
        double scale = Math.Min(1, 4096d / Math.Max(image.Width, image.Height));
        var bitmap = new Bitmap(Math.Max(1, (int)(image.Width * scale)), Math.Max(1, (int)(image.Height * scale)),
            PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(image, new Rectangle(Point.Empty, bitmap.Size));
        return bitmap;
    }
}
