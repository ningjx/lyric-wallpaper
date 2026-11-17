using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

internal sealed class DesktopWindow : NativeWindow, IDisposable
{
    private const int Child = 0x40000000, Popup = unchecked((int)0x80000000);
    private const int Layered = 0x80000, NoRedirectionBitmap = 0x200000;
    private const uint NoActivate = 0x10, Show = 0x40;
    public readonly record struct Layer(nint Parent, nint Icons, nint Backdrop, bool Raised);
    public bool ClosedByUser { get; private set; }
    private readonly bool preview;

    public DesktopWindow(Layer layer, bool preview)
    {
        this.preview = preview;
        nint previousDpi = layer.Parent == 0 ? 0 :
            SetThreadDpiAwarenessContext(GetWindowDpiAwarenessContext(layer.Parent));
        try
        {
            CreateHandle(new CreateParams
            {
                Caption = "Native Lyrics Wallpaper",
                Style = Popup | 0x06000000, // clip siblings and children
                ExStyle = preview ? 0x40000 : 0x08000080 | (layer.Raised ? Layered : 0),
                Width = 1,
                Height = 1
            });
            if (layer.Raised && !SetLayeredWindowAttributes(Handle, 0, 255, 2))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetLayeredWindowAttributes");
            if (layer.Parent != 0)
            {
                int style = GetWindowLong(Handle, -16);
                Marshal.SetLastPInvokeError(0);
                if (SetWindowLong(Handle, -16, (style & ~Popup) | Child) == 0 && Marshal.GetLastWin32Error() != 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "set wallpaper window style");
                SetParent(Handle, layer.Parent);
                int error = Marshal.GetLastWin32Error();
                if (GetParent(Handle) != layer.Parent)
                    throw new Win32Exception(error, "attach window to Explorer");
            }
        }
        catch
        {
            DestroyHandle();
            throw;
        }
        finally
        {
            if (previousDpi != 0) SetThreadDpiAwarenessContext(previousDpi);
        }
    }

    public static Layer? FindLayer()
    {
        nint progman = FindWindow("Progman", null);
        if (progman == 0) return null;
        var layer = ReadLayer(progman);
        if (layer is not null) return layer;

        SendMessageTimeout(progman, 0x052c, 0xD, 1, 2, 500, out _);
        return ReadLayer(progman);
    }

    private static Layer? ReadLayer(nint progman)
    {
        if ((GetWindowLong(progman, -20) & NoRedirectionBitmap) != 0)
        {
            nint icons = FindWindowEx(progman, 0, "SHELLDLL_DefView", null);
            nint backdrop = icons == 0 ? 0 : FindWindowEx(progman, icons, "WorkerW", null);
            return backdrop == 0 ? null : new Layer(progman, icons, backdrop, true);
        }

        Layer? result = null;
        EnumWindows((top, _) =>
        {
            nint icons = FindWindowEx(top, 0, "SHELLDLL_DefView", null);
            if (icons == 0) return true;
            nint backdrop = FindWindowEx(0, top, "WorkerW", null);
            // An empty sibling WorkerW underneath the window that owns the icons.
            if (backdrop == 0 || FindWindowEx(backdrop, 0, "SHELLDLL_DefView", null) != 0)
                return true;
            result = new Layer(backdrop, icons, backdrop, false);
            return false;
        }, 0);
        return result;
    }

    public void Position(Layer layer, Size size, bool visible)
    {
        var point = new Point(0, 0); // Origin of the primary display in virtual screen coordinates.
        nint previousDpi = layer.Parent == 0 ? 0 :
            SetThreadDpiAwarenessContext(GetWindowDpiAwarenessContext(layer.Parent));
        try
        {
            if (layer.Parent != 0) MapWindowPoints(0, layer.Parent, ref point, 1);
            if (!SetWindowPos(Handle, layer.Raised ? layer.Icons : 0, point.X, point.Y,
                    size.Width, size.Height, NoActivate | (visible ? Show : 0)))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "position wallpaper window");
        }
        finally
        {
            if (previousDpi != 0) SetThreadDpiAwarenessContext(previousDpi);
        }
    }

    public Size ClientSize
    {
        get
        {
            GetClientRect(Handle, out Rect rect);
            return new Size(rect.Right - rect.Left, rect.Bottom - rect.Top);
        }
    }

    public bool IsAlive => Handle != 0 && IsWindow(Handle);
    public bool IsBehindIcons(Layer layer) =>
        IsAlive && IsWindow(layer.Icons) && IsWindow(layer.Backdrop) && GetParent(Handle) == layer.Parent &&
        (layer.Raised ? IsAbove(layer.Icons, Handle) && IsAbove(Handle, layer.Backdrop)
                      : IsAbove(GetParent(layer.Icons), layer.Parent));

    public bool IsShown => IsAlive && IsWindowVisible(Handle);

    private static bool IsAbove(nint upper, nint lower)
    {
        for (nint sibling = GetWindow(upper, 2); sibling != 0; sibling = GetWindow(sibling, 2))
            if (sibling == lower) return true;
        return false;
    }

    protected override void WndProc(ref Message message)
    {
        switch (message.Msg)
        {
            case 0x0010: // WM_CLOSE
            case 0x0100 when preview && message.WParam == 27: // Escape
                ClosedByUser = true;
                return;
            case 0x0084 when !preview: // HTTRANSPARENT
                message.Result = -1;
                return;
            case 0x0021 when !preview: // MA_NOACTIVATE
                message.Result = 3;
                return;
            default:
                base.WndProc(ref message);
                return;
        }
    }

    public void Dispose() => DestroyHandle();

    public sealed class MessageWait : IDisposable
    {
        private readonly SafeWaitHandle timer = CreateWaitableTimerEx(0, null, 2, 0x100002);

        public unsafe void Until(double seconds, WaitHandle? changed, WaitHandle? stopping = null)
        {
            if (seconds <= 0) return;
            uint count = 0;
            nint* handles = stackalloc nint[3];
            if (changed is not null) handles[count++] = changed.SafeWaitHandle.DangerousGetHandle();
            if (stopping is not null) handles[count++] = stopping.SafeWaitHandle.DangerousGetHandle();
            // The standard Win32 wait can round up to ~15 ms. Use a high-resolution
            // timer for frame pacing without changing the system timer resolution.
            if (seconds <= .04 && !timer.IsInvalid)
            {
                long due = -(long)Math.Ceiling(seconds * TimeSpan.TicksPerSecond);
                if (SetWaitableTimer(timer, in due, 0, 0, 0, false))
                {
                    handles[count++] = timer.DangerousGetHandle();
                    MsgWaitForMultipleObjectsEx(count, handles, uint.MaxValue, 0x04FF, 4);
                    return;
                }
            }
            uint timeout = double.IsPositiveInfinity(seconds) ? uint.MaxValue : (uint)Math.Ceiling(seconds * 1000);
            MsgWaitForMultipleObjectsEx(count, handles, timeout, 0x04FF, 4);
        }

        public void Dispose() => timer.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    private delegate bool EnumWindow(nint window, nint state);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint after, string className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SetWindowLong(nint window, int index, int value);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetParent(nint child, nint newParent);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindow callback, nint state);
    [DllImport("user32.dll")]
    private static extern nint SendMessageTimeout(nint window, uint message, nint wParam, nint lParam,
        uint flags, uint timeout, out nint result);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(nint window, uint key, byte alpha, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern int MapWindowPoints(nint from, nint to, ref Point point, uint count);
    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")]
    private static extern nint GetParent(nint window);
    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]
    private static extern nint GetWindowDpiAwarenessContext(nint window);
    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")]
    private static extern unsafe uint MsgWaitForMultipleObjectsEx(uint count, nint* handles, uint milliseconds, uint mask, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern SafeWaitHandle CreateWaitableTimerEx(nint attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll")]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, in long due, int period, nint completion, nint state, bool resume);
}
