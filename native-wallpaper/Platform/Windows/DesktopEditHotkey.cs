using System.Runtime.InteropServices;

namespace LyricWallpaper.Platform.Windows;

internal sealed class DesktopEditHotkey : NativeWindow, IDisposable
{
    private const int HotkeyId = 1;
    private readonly Action toggle;
    public bool Registered { get; }

    public DesktopEditHotkey(Action toggle)
    {
        this.toggle = toggle;
        CreateHandle(new CreateParams { Parent = -3 }); // HWND_MESSAGE
        Registered = RegisterHotKey(Handle, HotkeyId, 0x4003, (uint)Keys.E); // Ctrl+Alt, suppress repeats
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0312 && message.WParam == HotkeyId)
        {
            toggle();
            return;
        }
        base.WndProc(ref message);
    }

    public void Dispose()
    {
        if (Registered) UnregisterHotKey(Handle, HotkeyId);
        DestroyHandle();
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint window, int id);
}
