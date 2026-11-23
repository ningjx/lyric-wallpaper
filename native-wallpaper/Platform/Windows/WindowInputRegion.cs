using System.Runtime.InteropServices;

namespace LyricWallpaper.Platform.Windows;

internal sealed class WindowInputRegion
{
    private Point origin;
    private Rectangle[] lastControls = [], lastIcons = [], lastOverlays = [];

    public void Update(nint window, Rectangle[] controls, Rectangle[] icons, Rectangle[] overlays)
    {
        if (!GetWindowRect(window, out var bounds)) return;
        var nextOrigin = new Point(bounds.Left, bounds.Top);
        if (origin == nextOrigin && ReferenceEquals(lastIcons, icons) && controls.AsSpan().SequenceEqual(lastControls) &&
            overlays.AsSpan().SequenceEqual(lastOverlays)) return;

        nint region = CreateRectRgn(0, 0, 0, 0);
        if (region == 0) return;
        foreach (var control in controls) Combine(control, 2); // RGN_OR
        foreach (var icon in icons)
            if (Array.Exists(controls, control => control.IntersectsWith(icon))) Combine(icon, 4); // RGN_DIFF
        foreach (var overlay in overlays) Combine(overlay, 2);
        if (SetWindowRgn(window, region, true) != 0)
            (origin, lastControls, lastIcons, lastOverlays) = (nextOrigin, controls, icons, overlays);
        else DeleteObject(region);

        void Combine(Rectangle rect, int operation)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return;
            nint part = CreateRectRgn(rect.Left - nextOrigin.X, rect.Top - nextOrigin.Y,
                rect.Right - nextOrigin.X, rect.Bottom - nextOrigin.Y);
            if (part == 0) return;
            CombineRgn(region, region, part, operation);
            DeleteObject(part);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out NativeRect rectangle);
    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(nint window, nint region, bool redraw);
    [DllImport("gdi32.dll")]
    private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(nint target, nint first, nint second, int operation);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint handle);
}
