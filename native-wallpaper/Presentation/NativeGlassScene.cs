using System.Diagnostics;
using System.Numerics;
using System.Windows.Interop;
using System.Windows.Threading;
using LyricWallpaper.Rendering;
using W = System.Windows;
using Ui = System.Windows.Controls;
using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;

namespace LyricWallpaper.Presentation;

internal interface INativeGlassHost
{
    void InvalidateGlass();
}

internal interface INativeGlassControl
{
    bool Compose(GlassFrame frame, W.Rect bounds, W.Rect clip, double elapsed);
}

internal readonly record struct GlassLabel(string Text, W.Rect Bounds, W.Rect Clip, double Size, string? Icon = null, bool IsSelected = false);

internal sealed class GlassFrame(float dpi)
{
    public List<SettingsPrimitive> Surfaces { get; } = [];
    public List<SettingsPrimitive> Paint { get; } = [];
    public List<SettingsPrimitive> Lenses { get; } = [];
    public List<GlassLabel> Labels { get; } = [];
    public void Reset()
    {
        Surfaces.Clear(); Paint.Clear(); Lenses.Clear(); Labels.Clear();
    }

    public void Shape(List<SettingsPrimitive> target, W.Rect rect, W.Rect clip, Vector4 color, float radius,
        Vector3 optics = default, Vector4 style = default, Vector4 sampling = default,
        Vector4 track = default, Vector4 trackColor = default)
    {
        if (rect.Width <= 0 || rect.Height <= 0 || clip.IsEmpty) return;
        if (style == Vector4.Zero) style = new(1, 1, 0, 0);
        float halfX = (float)rect.Width * .5f, halfY = (float)rect.Height * .5f;
        target.Add(new(
            new Vector4((float)rect.X + halfX, (float)rect.Y + halfY, halfX, halfY) * dpi,
            new Vector4((float)clip.Left, (float)clip.Top, (float)clip.Right, (float)clip.Bottom) * dpi,
            color, new Vector4(MathF.Min(radius, MathF.Min(halfX, halfY)), optics.X, optics.Y, optics.Z) * dpi,
            style, sampling, track * dpi, trackColor));
    }
}

internal sealed class NativeGlassScene : IDisposable
{
    private static readonly W.Duration NoWait = new(TimeSpan.Zero);
    private const double ResizeInterval = 1d / 30;
    private readonly W.Window owner;
    private readonly W.FrameworkElement content;
    private readonly Ui.Image image;
    private readonly SettingsLabelAtlas labelAtlas = new();
    private List<SettingsLabel> labels = [], drawnLabels = [];
    private readonly DispatcherTimer resizeTimer = new(DispatcherPriority.Render);
    private D3DImage sharedImage = new();
    private readonly Stopwatch time = Stopwatch.StartNew();
    private D3D11Renderer.SettingsSurface? renderer;
    private Imaging.WriteableBitmap? softwareImage;
    private GlassFrame frame = new(1);
    private GlassFrame drawnFrame = new(1);
    private Rectangle sharedDirty;
    private int width, height;
    private float dpi;
    private bool scheduled, closed, hasSharedSurface, needsPresent;
    private int delayedPresents;
    private double lastFrame, lastResize;
    private TimeSpan lastRenderingTime = TimeSpan.MinValue;

    public NativeGlassScene(W.Window owner, W.FrameworkElement content)
    {
        this.owner = owner;
        this.content = content;
        image = new() { Stretch = Media.Stretch.Fill };
        var overlay = new Ui.Canvas { IsHitTestVisible = false };
        var canvas = new Ui.Grid { ClipToBounds = true };
        // The GPU image follows the measured content, including in size-to-content dialogs.
        image.SetBinding(W.FrameworkElement.WidthProperty, new W.Data.Binding(nameof(W.FrameworkElement.ActualWidth)) { Source = canvas });
        image.SetBinding(W.FrameworkElement.HeightProperty, new W.Data.Binding(nameof(W.FrameworkElement.ActualHeight)) { Source = canvas });
        overlay.Children.Add(image);
        canvas.Children.Add(overlay);
        if (ReferenceEquals(owner.Content, content)) owner.Content = null;
        canvas.Children.Add(content);
        owner.Content = canvas;

        owner.Loaded += (_, _) => Invalidate();
        owner.StateChanged += Changed;
        owner.Activated += Changed;
        owner.IsVisibleChanged += VisibilityChanged;
        owner.Closed += (_, _) => Dispose();
        content.LayoutUpdated += Changed;
        content.AddHandler(Ui.ScrollViewer.ScrollChangedEvent, new Ui.ScrollChangedEventHandler(Scrolled), true);
        sharedImage.IsFrontBufferAvailableChanged += VisibilityChanged;
        resizeTimer.Tick += (_, _) =>
        {
            resizeTimer.Stop();
            Invalidate();
        };
    }

    private void Changed(object? sender, EventArgs e) => Invalidate();
    private void VisibilityChanged(object sender, W.DependencyPropertyChangedEventArgs e)
    {
        if (owner.IsVisible) Invalidate();
        else
        {
            resizeTimer.Stop();
            Stop();
        }
    }
    private void Scrolled(object sender, Ui.ScrollChangedEventArgs e) => Invalidate();

    public void Invalidate()
    {
        if (closed || scheduled || resizeTimer.IsEnabled || !owner.IsLoaded || !owner.IsVisible ||
            owner.WindowState == W.WindowState.Minimized) return;
        lastFrame = time.Elapsed.TotalSeconds - 1d / 60;
        scheduled = true;
        Media.CompositionTarget.Rendering += Render;
    }

    private void Render(object? sender, EventArgs e)
    {
        if (closed || !owner.IsVisible || owner.WindowState == W.WindowState.Minimized)
        {
            Stop();
            return;
        }
        if (content.ActualWidth <= 0 || content.ActualHeight <= 0) return;
        if (e is Media.RenderingEventArgs timing)
        {
            if (lastRenderingTime == timing.RenderingTime) return;
            lastRenderingTime = timing.RenderingTime;
        }
        var scale = Media.VisualTreeHelper.GetDpi(content);
        int w = Math.Max(1, (int)Math.Ceiling(content.ActualWidth * scale.DpiScaleX));
        int h = Math.Max(1, (int)Math.Ceiling(content.ActualHeight * scale.DpiScaleY));
        bool resized = w != width || h != height;
        bool changedDpi = dpi != (float)scale.DpiScaleX;
        double now = time.Elapsed.TotalSeconds;
        double resizeDelay = lastResize + ResizeInterval - now;
        if (resized && !changedDpi && resizeDelay > 0)
        {
            // Keep the last image during the burst and redraw the latest size when the timer fires.
            resizeTimer.Interval = TimeSpan.FromSeconds(resizeDelay);
            resizeTimer.Start();
            Stop();
            return;
        }
        renderer ??= new(new WindowInteropHelper(owner).Handle);
        if (resized)
        {
            ReleaseImage();
            renderer.Resize(w, h);
            (width, height, lastResize) = (w, h, now);
            PresentSurface();
        }
        else if (hasSharedSurface != (renderer.Surface9 != 0 && sharedImage.IsFrontBufferAvailable))
        {
            ReleaseImage();
            PresentSurface();
            needsPresent = true;
        }
        if (changedDpi)
        {
            dpi = (float)scale.DpiScaleX;
            frame = new(dpi);
            drawnFrame = new(dpi);
        }
        double elapsed = Math.Clamp(now - lastFrame, 0, .05);
        lastFrame = now;
        frame.Reset();
        bool more = Collect(content, new W.Rect(new W.Point(), content.RenderSize), elapsed);
        DrawFrame(new Rectangle(0, 0, w, h), resized || changedDpi);
        if (needsPresent) needsPresent = !PublishFrame();
        if (!more && !needsPresent) Stop();
    }

    private void DrawFrame(Rectangle viewport, bool full)
    {
        // A window move can raise LayoutUpdated while all window-local geometry stays the same.
        bool relayout = full || !frame.Labels.SequenceEqual(drawnFrame.Labels);
        bool uploaded = relayout && labelAtlas.Layout(renderer!, frame.Labels, dpi, labels);
        var visibleLabels = relayout ? labels : drawnLabels;
        var baseDirty = viewport;
        var outputDirty = viewport;
        if (!full)
        {
            baseDirty = SettingsDamage.Union(
                SettingsDamage.Changed(drawnFrame.Surfaces, frame.Surfaces, viewport, SettingsDamage.Bounds),
                SettingsDamage.Changed(drawnFrame.Paint, frame.Paint, viewport, SettingsDamage.Bounds));
            if (relayout)
                baseDirty = SettingsDamage.Union(baseDirty,
                    SettingsDamage.Changed(drawnLabels, visibleLabels, viewport, SettingsDamage.Bounds, all: uploaded));
            outputDirty = SettingsDamage.Output(baseDirty, drawnFrame.Lenses, frame.Lenses, viewport);
        }
        if (!outputDirty.IsEmpty)
        {
            renderer!.Draw(dpi, frame.Surfaces, frame.Paint, frame.Lenses, visibleLabels, baseDirty, outputDirty);
            sharedDirty = SettingsDamage.Union(sharedDirty, outputDirty);
            needsPresent = true;
        }
        if (relayout) (labels, drawnLabels) = (drawnLabels, labels);
        (frame, drawnFrame) = (drawnFrame, frame);
    }

    private bool PublishFrame()
    {
        if (hasSharedSurface && sharedImage.TryLock(NoWait))
        {
            // A newly attached WPF image needs a full copy even when the GPU output is already current.
            var sharedBounds = ReferenceEquals(image.Source, sharedImage) ? sharedDirty : new Rectangle(0, 0, width, height);
            try
            {
                renderer!.PresentShared(sharedBounds);
                sharedImage.AddDirtyRect(ImageRect(sharedBounds));
            }
            finally { sharedImage.Unlock(); }
            sharedDirty = Rectangle.Empty;
            delayedPresents = 0;
            if (!ReferenceEquals(image.Source, sharedImage)) image.Source = sharedImage;
            return true;
        }
        if (hasSharedSurface && ++delayedPresents <= 2) return false;
        if (softwareImage is null)
        {
            softwareImage = new(width, height, 96, 96, Media.PixelFormats.Pbgra32, null);
            renderer!.ResetReadback();
        }
        renderer!.QueueReadback();
        if (!softwareImage.TryLock(NoWait)) return false;
        bool current, updated;
        try
        {
            updated = renderer.TryReadPixels(softwareImage.BackBuffer, softwareImage.BackBufferStride, out var dirty, out current);
            if (updated) softwareImage.AddDirtyRect(ImageRect(dirty));
        }
        finally { softwareImage.Unlock(); }
        if ((updated || current) && !ReferenceEquals(image.Source, softwareImage)) image.Source = softwareImage;
        return current;
    }

    private static W.Int32Rect ImageRect(Rectangle rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    private bool Collect(W.DependencyObject element, W.Rect clip, double elapsed)
    {
        if (element is W.UIElement { IsVisible: false }) return false;
        if (element is Ui.ScrollContentPresenter viewport)
        {
            clip.Intersect(Bounds(viewport));
            if (clip.IsEmpty) return false;
        }
        bool more = false;
        if (element is INativeGlassControl control && element is W.FrameworkElement item)
        {
            W.Rect bounds = Bounds(item);
            if (bounds.IntersectsWith(clip)) more = control.Compose(frame, bounds, clip, elapsed);
        }
        for (int child = 0, count = Media.VisualTreeHelper.GetChildrenCount(element); child < count; child++)
            more |= Collect(Media.VisualTreeHelper.GetChild(element, child), clip, elapsed);
        return more;
    }

    private W.Rect Bounds(W.FrameworkElement element) =>
        element.TransformToAncestor(content).TransformBounds(new W.Rect(new W.Point(), element.RenderSize));

    private void PresentSurface()
    {
        nint surface = renderer!.Surface9;
        if (surface != 0 && sharedImage.IsFrontBufferAvailable)
        {
            sharedImage.Lock();
            try
            {
                sharedImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, surface, true);
                hasSharedSurface = true;
            }
            finally { sharedImage.Unlock(); }
        }
    }

    private void ReleaseImage()
    {
        if (hasSharedSurface)
        {
            if (sharedImage.TryLock(new W.Duration(TimeSpan.FromMilliseconds(5))))
            {
                try { sharedImage.SetBackBuffer(D3DResourceType.IDirect3DSurface9, 0); }
                finally { sharedImage.Unlock(); }
            }
            else
            {
                sharedImage.IsFrontBufferAvailableChanged -= VisibilityChanged;
                sharedImage = new();
                sharedImage.IsFrontBufferAvailableChanged += VisibilityChanged;
            }
        }
        hasSharedSurface = false;
        sharedDirty = Rectangle.Empty;
        delayedPresents = 0;
        image.Source = null;
        softwareImage = null;
    }

    private void Stop()
    {
        Media.CompositionTarget.Rendering -= Render;
        scheduled = false;
    }

    public void Dispose()
    {
        if (closed) return;
        closed = true;
        resizeTimer.Stop();
        Stop();
        ReleaseImage();
        renderer?.Dispose();
        renderer = null;
    }
}
