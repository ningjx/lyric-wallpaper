using System.Runtime.InteropServices;

namespace LyricWallpaper.Platform.Windows;

internal sealed class WidgetEditorMouseInput : IDisposable
{
    private const int MouseHook = 14; // WH_MOUSE_LL
    private const int MouseMove = 0x0200, LeftDown = 0x0201, LeftUp = 0x0202;
    private readonly HookCallback callback;
    private readonly Func<Point, bool> isOutside;
    private readonly Action clicked;
    private readonly Size dragSize = SystemInformation.DragSize;
    private nint hook;
    private Point? press;
    private Action<Point, bool>? heldPress;

    public WidgetEditorMouseInput(Func<Point, bool> isOutside, Action clicked)
    {
        this.isOutside = isOutside;
        this.clicked = clicked;
        callback = OnMouse;
        hook = SetWindowsHookEx(MouseHook, callback, GetModuleHandle(null), 0);
    }

    public bool FollowHeldPress(Action<Point, bool> update)
    {
        if (hook == 0) return false;
        press = null;
        heldPress = update;
        return true;
    }

    public void StopFollowingHeldPress() => heldPress = null;

    public static bool HitsAppControls(Point point, nint wallpaper)
    {
        var hit = WindowFromPoint(point);
        if (hit == 0 || hit == wallpaper) return false;
        GetWindowThreadProcessId(hit, out int process);
        return process == Environment.ProcessId;
    }

    private nint OnMouse(int code, nint message, nint data)
    {
        if (code == 0)
        {
            switch ((int)message)
            {
                case LeftDown:
                    var down = ReadPoint(data);
                    press = isOutside(down) ? down : null;
                    break;
                case MouseMove when heldPress is { } update:
                    update(ReadPoint(data), false);
                    break;
                case LeftUp when heldPress is { } update:
                    heldPress = null;
                    update(ReadPoint(data), true);
                    break;
                case MouseMove when press is { } start && !WithinClick(start, ReadPoint(data)):
                    press = null;
                    break;
                case LeftUp when press is { } start:
                    press = null;
                    var up = ReadPoint(data);
                    if (WithinClick(start, up) && isOutside(up)) clicked();
                    break;
            }
        }
        // Leave the original click available to the window underneath the editor.
        return CallNextHookEx(0, code, message, data);
    }

    private bool WithinClick(Point start, Point end) =>
        Math.Abs(end.X - start.X) <= dragSize.Width / 2 && Math.Abs(end.Y - start.Y) <= dragSize.Height / 2;

    // The first field of MSLLHOOKSTRUCT is a screen-space POINT.
    private static Point ReadPoint(nint data) => new(Marshal.ReadInt32(data), Marshal.ReadInt32(data, sizeof(int)));

    public void Dispose()
    {
        if (hook == 0) return;
        UnhookWindowsHookEx(hook);
        hook = 0;
        press = null;
        heldPress = null;
    }

    private delegate nint HookCallback(int code, nint message, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SetWindowsHookEx(int type, HookCallback callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out int process);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);
}
