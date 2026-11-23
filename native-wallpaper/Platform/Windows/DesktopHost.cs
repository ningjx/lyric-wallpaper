namespace LyricWallpaper.Platform.Windows;

internal readonly record struct DesktopTarget(DesktopWindow.Layer Layer, Rectangle Display, Rectangle WorkArea);

internal sealed class DesktopHost(bool preview) : IDisposable
{
    private DesktopTarget? attached;
    public DesktopWindow? Window { get; private set; }

    public DesktopTarget? Probe()
    {
        var layer = preview ? new DesktopWindow.Layer() : DesktopWindow.FindLayer();
        if (layer is not { } found) return null;
        var screen = Screen.PrimaryScreen ?? throw new InvalidOperationException("No primary display");
        return new(found, screen.Bounds, screen.WorkingArea);
    }

    public bool NeedsAttachment(DesktopTarget target) => Window is not { IsAlive: true } ||
        attached?.Layer != target.Layer || attached?.Display.Size != target.Display.Size;

    public DesktopWindow Attach(DesktopTarget target)
    {
        Detach();
        attached = target;
        var window = new DesktopWindow(target.Layer, preview);
        Window = window;
        window.Position(target.Layer, target.Display.Size, false);
        return window;
    }

    public void Reveal(DesktopTarget target) => Window?.Position(target.Layer, target.Display.Size, true);

    public bool MaintainPlacement(DesktopTarget target)
    {
        if (Window is not { } window) return false;
        if (!window.IsBehindIcons(target.Layer) || !window.IsShown) Reveal(target);
        return window.IsBehindIcons(target.Layer) && window.IsShown;
    }

    public void Detach()
    {
        Window?.Dispose();
        Window = null;
        attached = null;
    }

    public void Dispose() => Detach();
}
