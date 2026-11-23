using LyricWallpaper.Scene;

namespace LyricWallpaper.Platform.Windows;

internal readonly record struct WidgetEditorLayout(Rectangle Display, Rectangle WorkArea, WidgetLayout[] Items, nint IconView, nint WallpaperView);
internal readonly record struct WidgetHold(Guid Id, Point Pointer);

internal sealed class WidgetDesktopInput(bool preview) : IDisposable
{
    private WidgetInputWindow? window;
    private (WidgetHold Hold, double Due)? hold;
    private double nextAttachmentCheck;

    public Guid? Poll(DesktopWindow desktop, DesktopWindow.Layer layer, WidgetLayout[] layouts, double now, Action<WidgetHold> beginEdit)
    {
        if (now >= nextAttachmentCheck)
        {
            nextAttachmentCheck = now + 1;
            nint parent = WidgetInputWindow.ParentFor(layer, desktop, preview);
            if (parent != 0 && window?.IsAttachedTo(parent) != true)
            {
                hold = null;
                window?.Dispose();
                window = new WidgetInputWindow(parent, preview ? 0 : layer.Icons);
            }
        }
        if (window is null) return null;
        var (down, up) = window.TakePointerEvents();
        if (down is { } start)
        {
            hold = null;
            int hit = Array.FindLastIndex(layouts, item => item.Contains(start));
            if (hit >= 0) hold = (new WidgetHold(layouts[hit].Id, start), now + .35);
        }
        if (hold is not { } pressed) return null;
        var pointer = up ?? Cursor.Position;
        var tolerance = SystemInformation.DragSize;
        if (Math.Abs(pointer.X - pressed.Hold.Pointer.X) > tolerance.Width / 2 ||
            Math.Abs(pointer.Y - pressed.Hold.Pointer.Y) > tolerance.Height / 2)
        {
            hold = null;
        }
        else if (up is not null)
        {
            hold = null;
            return pressed.Hold.Id;
        }
        else if (!window.LeftButtonDown) hold = null;
        else if (now >= pressed.Due)
        {
            hold = null;
            window.Release();
            beginEdit(pressed.Hold);
        }
        return null;
    }

    public double Place(WidgetLayout[] layouts, double now)
    {
        if (window is null) return double.PositiveInfinity;
        window.Place(layouts, now);
        double next = window.NextWake(now);
        return hold is { } pressed ? Math.Min(next, Math.Min(pressed.Due, now + 1.0 / 60)) : next;
    }

    public void Reset()
    {
        hold = null;
        window?.Dispose();
        window = null;
        nextAttachmentCheck = 0;
    }

    public void Dispose() => Reset();
}
