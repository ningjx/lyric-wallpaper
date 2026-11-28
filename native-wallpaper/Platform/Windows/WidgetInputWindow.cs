using System.ComponentModel;
using System.Runtime.InteropServices;
using Accessibility;
using LyricWallpaper.Scene;

namespace LyricWallpaper.Platform.Windows;

internal sealed class WidgetInputWindow : NativeWindow, IDisposable
{
    private const int Popup = unchecked((int)0x80000000), Child = 0x40000000, ClipSiblings = 0x04000000;
    private readonly nint parent, iconList;
    private Rectangle[]? icons;
    private Task<Rectangle[]?>? iconQuery;
    private double nextIconCheck;
    private Rectangle shownBounds;
    private WidgetLayout[] shownCards = [];
    private Point? pressedAt, releasedAt;
    private bool holding;
    public static bool IsLeftButtonDown => (GetAsyncKeyState(1) & 0x8000) != 0;
    public bool LeftButtonDown => holding && IsLeftButtonDown;
    public double NextWake(double now) =>
        Handle == 0 ? double.PositiveInfinity : iconQuery is null ? nextIconCheck : now + .03;

    public static nint ParentFor(DesktopWindow.Layer layer, DesktopWindow wallpaper, bool preview) =>
        preview ? wallpaper.Handle : IsWindowVisible(layer.Icons) ? layer.Icons : GetParent(layer.Icons);

    public WidgetInputWindow(nint parent, nint iconView)
    {
        this.parent = parent;
        iconList = iconView == 0 ? 0 : FindWindowEx(iconView, 0, "SysListView32", null);
        nint previousDpi = SetThreadDpiAwarenessContext(GetWindowDpiAwarenessContext(parent));
        try
        {
            // Explorer owns the parent, so attach after creating the HWND in this process.
            CreateHandle(new CreateParams { Style = Popup | ClipSiblings, ExStyle = 0x08080080, Width = 1, Height = 1 });
            SetWindowLong(Handle, -16, Child | ClipSiblings);
            SetParent(Handle, parent);
            if (GetParent(Handle) != parent || !SetLayeredWindowAttributes(Handle, 0, 1, 2))
                DestroyHandle();
        }
        catch (Win32Exception)
        {
            DestroyHandle();
        }
        finally
        {
            if (previousDpi != 0) SetThreadDpiAwarenessContext(previousDpi);
        }
    }

    public bool IsAttachedTo(nint expectedParent) => parent == expectedParent && Handle != 0 && IsWindow(Handle);

    public (Point? Down, Point? Up) TakePointerEvents()
    {
        var events = (pressedAt, releasedAt);
        pressedAt = releasedAt = null;
        return events;
    }

    public void Place(WidgetLayout[] cards, double now)
    {
        if (Handle == 0) return;
        if (iconQuery is { IsCompleted: true })
        {
            var refreshed = iconQuery.GetAwaiter().GetResult();
            if (refreshed is null || icons is null || !icons.SequenceEqual(refreshed)) shownBounds = Rectangle.Empty;
            icons = refreshed;
            iconQuery = null;
        }
        if (iconQuery is null && now >= nextIconCheck)
        {
            nextIconCheck = now + 1;
            if (iconList == 0 || !IsWindowVisible(iconList))
            {
                if (icons is not { Length: 0 }) shownBounds = Rectangle.Empty;
                icons = [];
            }
            else iconQuery = Task.Run(() => ReadIconBounds(iconList));
        }
        if (icons is null || cards.Length == 0)
        {
            Hide();
            return;
        }
        if (!shownBounds.IsEmpty && cards.AsSpan().SequenceEqual(shownCards)) return;

        // Keep the icon cells and labels clickable where they overlap the clock.
        Rectangle bounds = Rectangle.Ceiling(cards[0].Bounds);
        foreach (var card in cards.AsSpan(1)) bounds = Rectangle.Union(bounds, Rectangle.Ceiling(card.Bounds));
        nint region = CreateRectRgn(0, 0, 0, 0);
        if (region == 0) return;
        foreach (var card in cards)
        {
            var rect = Rectangle.Ceiling(card.Bounds);
            rect.Offset(-bounds.X, -bounds.Y);
            nint part = card.Radius > 0
                ? CreateRoundRectRgn(rect.Left, rect.Top, rect.Right + 1, rect.Bottom + 1, 2 * card.Radius, 2 * card.Radius)
                : CreateRectRgn(rect.Left, rect.Top, rect.Right, rect.Bottom);
            if (part == 0) continue;
            CombineRgn(region, region, part, 2);
            DeleteObject(part);
        }
        foreach (var icon in icons)
        {
            var overlap = Rectangle.Intersect(bounds, icon);
            if (overlap.IsEmpty) continue;
            nint cutout = CreateRectRgn(overlap.Left - bounds.Left, overlap.Top - bounds.Top,
                overlap.Right - bounds.Left, overlap.Bottom - bounds.Top);
            if (cutout == 0) continue;
            CombineRgn(region, region, cutout, 4);
            DeleteObject(cutout);
        }
        if (SetWindowRgn(Handle, region, false) == 0)
        {
            DeleteObject(region);
            return;
        }

        var point = bounds.Location;
        nint previousDpi = SetThreadDpiAwarenessContext(GetWindowDpiAwarenessContext(parent));
        try
        {
            MapWindowPoints(0, parent, ref point, 1);
            if (SetWindowPos(Handle, 0, point.X, point.Y, bounds.Width, bounds.Height, 0x50))
                (shownBounds, shownCards) = (bounds, cards);
        }
        finally
        {
            if (previousDpi != 0) SetThreadDpiAwarenessContext(previousDpi);
        }
    }

    internal static Rectangle[]? ReadDesktopIconBounds(nint iconView)
    {
        if (iconView == 0) return [];
        var list = FindWindowEx(iconView, 0, "SysListView32", null);
        return list == 0 || !IsWindowVisible(list) ? [] : ReadIconBounds(list);
    }

    private static Rectangle[]? ReadIconBounds(nint iconList)
    {
        IAccessible? owner = null;
        object[] children = [];
        try
        {
            // Poll the native list directly. Repeated UI Automation tree walks retain a
            // client-side ListViewItem proxy and its native peer for each icon on every poll.
            const uint ObjIdClient = 0xFFFFFFFC;
            var iid = typeof(IAccessible).GUID;
            if (AccessibleObjectFromWindow(iconList, ObjIdClient, in iid, out owner) < 0 || owner is null) return null;
            int count = owner.accChildCount;
            if (count == 0) return [];
            children = new object[count];
            if (AccessibleChildren(owner, 0, count, children, out int obtained) < 0) return null;
            var bounds = new List<Rectangle>(obtained);
            for (int i = 0; i < obtained; i++)
            {
                // MSAA returns either a child ID on the container or a separate child object.
                var item = owner;
                int id = 0; // CHILDID_SELF
                if (children[i] is int childId) id = childId;
                else if (children[i] is IAccessible accessible) item = accessible;
                else return null;
                if (item.get_accRole(id) is not (int)AccessibleRole.ListItem) continue;
                item.accLocation(out int x, out int y, out int width, out int height, id);
                if (width > 0 && height > 0) bounds.Add(new(x, y, width, height));
            }
            return bounds.ToArray();
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException or
            InvalidCastException or NotImplementedException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            foreach (var child in children)
                if (child is not null && Marshal.IsComObject(child)) Marshal.ReleaseComObject(child);
            if (owner is not null) Marshal.ReleaseComObject(owner);
        }
    }

    public void Release()
    {
        holding = false;
        if (GetCapture() == Handle && Handle != 0) ReleaseCapture();
    }

    public void Hide()
    {
        pressedAt = releasedAt = null;
        Release();
        if (Handle != 0) ShowWindow(Handle, 0);
        shownBounds = Rectangle.Empty;
    }

    protected override void WndProc(ref Message message)
    {
        switch (message.Msg)
        {
            case 0x0021: // WM_MOUSEACTIVATE
                message.Result = 3; // MA_NOACTIVATE
                return;
            case 0x0020: // WM_SETCURSOR
                SetCursor(Cursors.Hand.Handle);
                message.Result = 1;
                return;
            case 0x0201: // WM_LBUTTONDOWN
                holding = true;
                // Preserve this edge until the render loop consumes it, even after a quick release.
                pressedAt = Cursor.Position;
                releasedAt = null;
                SetCapture(Handle);
                return;
            case 0x0202: // WM_LBUTTONUP
                if (holding) releasedAt = Cursor.Position;
                Release();
                return;
            case 0x001F: // WM_CANCELMODE
            case 0x0215: // WM_CAPTURECHANGED
                Release();
                return;
            default:
                base.WndProc(ref message);
                return;
        }
    }

    public void Dispose()
    {
        Hide();
        DestroyHandle();
    }

    [DllImport("oleacc.dll")] private static extern int AccessibleObjectFromWindow(nint window, uint objectId,
        in Guid iid, [MarshalAs(UnmanagedType.Interface)] out IAccessible? accessible);
    [DllImport("oleacc.dll")] private static extern int AccessibleChildren(IAccessible container, int start, int count,
        [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.Struct, SizeParamIndex = 2)] object[] children,
        out int obtained);
    [DllImport("user32.dll")] private static extern nint GetParent(nint window);
    [DllImport("user32.dll")] private static extern nint SetParent(nint window, nint newParent);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowLong(nint window, int index, int value);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint after, string className, string? title);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(nint window, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint window, nint region, bool redraw);
    [DllImport("user32.dll")] private static extern int MapWindowPoints(nint from, nint to, ref Point point, uint count);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern nint GetWindowDpiAwarenessContext(nint window);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern nint SetCapture(nint window);
    [DllImport("user32.dll")] private static extern nint GetCapture();
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern nint SetCursor(nint cursor);
    [DllImport("gdi32.dll")] private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(nint target, nint first, nint second, int mode);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
}
