using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Windows.Interop;
using LyricWallpaper.Rendering;
using W = System.Windows;
using Ui = System.Windows.Controls;
using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;

namespace LyricWallpaper.Presentation;

internal interface INativeGlassControl
{
    bool Compose(GlassFrame frame, W.Rect bounds, W.Rect clip, double elapsed);
}

internal readonly record struct GlassLabel(string Text, W.Rect Bounds, W.Rect Clip, double Size, string? Icon = null, bool IsSelected = false);

internal sealed class GlassFrame(float dpi)
{
    public List<SettingsPrimitive> Docks { get; } = [];
    public List<SettingsPrimitive> Paint { get; } = [];
    public List<SettingsPrimitive> Lenses { get; } = [];
    public List<GlassLabel> Labels { get; } = [];
    public void Reset()
    {
        Docks.Clear(); Paint.Clear(); Lenses.Clear(); Labels.Clear();
    }

    public bool Matches(GlassFrame other) =>
        Docks.SequenceEqual(other.Docks) && Paint.SequenceEqual(other.Paint) &&
        Lenses.SequenceEqual(other.Lenses) && Labels.SequenceEqual(other.Labels);

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
    private readonly W.Window owner;
    private readonly W.FrameworkElement content;
    private readonly Ui.Image image;
    private D3DImage sharedImage = new();
    private readonly Stopwatch time = Stopwatch.StartNew();
    private D3D11Renderer.SettingsSurface? renderer;
    private Imaging.WriteableBitmap? softwareImage;
    private GlassFrame frame = new(1);
    private GlassFrame drawnFrame = new(1);
    private byte[] pixels = [];
    private int width, height;
    private float dpi;
    private bool scheduled, closed, hasSharedSurface, needsPresent;
    private int delayedPresents;
    private double lastFrame;

    public NativeGlassScene(W.Window owner, W.FrameworkElement content, Ui.Image image)
    {
        this.owner = owner;
        this.content = content;
        this.image = image;
        owner.Loaded += (_, _) => Invalidate();
        owner.StateChanged += Changed;
        owner.Activated += Changed;
        owner.IsVisibleChanged += VisibilityChanged;
        content.LayoutUpdated += Changed;
        content.AddHandler(Ui.ScrollViewer.ScrollChangedEvent, new Ui.ScrollChangedEventHandler(Scrolled), true);
        sharedImage.IsFrontBufferAvailableChanged += VisibilityChanged;
    }

    private void Changed(object? sender, EventArgs e) => Invalidate();
    private void VisibilityChanged(object sender, W.DependencyPropertyChangedEventArgs e)
    {
        if (owner.IsVisible) Invalidate();
        else Stop();
    }
    private void Scrolled(object sender, Ui.ScrollChangedEventArgs e) => Invalidate();

    public void Invalidate()
    {
        if (closed || scheduled || !owner.IsLoaded || !owner.IsVisible) return;
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
        renderer ??= new(new WindowInteropHelper(owner).Handle);
        var scale = Media.VisualTreeHelper.GetDpi(content);
        int w = Math.Max(1, (int)Math.Ceiling(content.ActualWidth * scale.DpiScaleX));
        int h = Math.Max(1, (int)Math.Ceiling(content.ActualHeight * scale.DpiScaleY));
        bool resized = w != width || h != height || dpi != (float)scale.DpiScaleX;
        if (resized)
        {
            ReleaseImage();
            renderer.Resize(w, h);
            (width, height, dpi) = (w, h, (float)scale.DpiScaleX);
            frame = new(dpi);
            drawnFrame = new(dpi);
            PresentSurface();
        }
        else if (hasSharedSurface != (renderer.Surface9 != 0 && sharedImage.IsFrontBufferAvailable))
        {
            ReleaseImage();
            PresentSurface();
            needsPresent = true;
        }
        double now = time.Elapsed.TotalSeconds;
        double elapsed = Math.Clamp(now - lastFrame, 0, .05);
        lastFrame = now;
        frame.Reset();
        bool more = Collect(content, new W.Rect(new W.Point(), content.RenderSize), elapsed);
        // A window move can raise LayoutUpdated while all window-local geometry stays the same.
        if (resized || !frame.Matches(drawnFrame))
        {
            if (!frame.Labels.SequenceEqual(drawnFrame.Labels)) UploadLabels();
            renderer.Draw(dpi, frame.Docks, frame.Paint, frame.Lenses);
            (frame, drawnFrame) = (drawnFrame, frame);
            needsPresent = true;
        }
        if (needsPresent) needsPresent = !PublishFrame();
        if (!more && !needsPresent) Stop();
    }

    private bool PublishFrame()
    {
        var dirty = new W.Int32Rect(0, 0, width, height);
        if (hasSharedSurface && sharedImage.TryLock(new W.Duration(TimeSpan.FromMilliseconds(1))))
        {
            try
            {
                renderer!.PresentShared();
                sharedImage.AddDirtyRect(dirty);
            }
            finally { sharedImage.Unlock(); }
            delayedPresents = 0;
            if (!ReferenceEquals(image.Source, sharedImage)) image.Source = sharedImage;
            return true;
        }
        if (hasSharedSurface && ++delayedPresents <= 2) return false;
        // The GPU frame remains current even when DWM skips an occluded shared surface.
        if (softwareImage is null)
        {
            softwareImage = new(width, height, 96, 96, Media.PixelFormats.Pbgra32, null);
            pixels = new byte[checked(width * height * 4)];
        }
        renderer!.ReadPixels(pixels);
        softwareImage.WritePixels(dirty, pixels, width * 4, 0);
        if (!ReferenceEquals(image.Source, softwareImage)) image.Source = softwareImage;
        return true;
    }

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

    private void UploadLabels()
    {
        var visual = new Media.DrawingVisual();
        var ink = new Media.SolidColorBrush(Media.Color.FromRgb(32, 46, 63));
        var selectedInk = new Media.SolidColorBrush(Media.Color.FromRgb(0, 84, 163));
        using (var dc = visual.RenderOpen())
        {
            foreach (var label in frame.Labels) DrawLabel(dc, label, label.IsSelected ? selectedInk : ink);
        }
        var bitmap = new Imaging.RenderTargetBitmap(width, height, 96, 96, Media.PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var textPixels = new byte[checked(width * height * 4)];
        bitmap.CopyPixels(textPixels, width * 4, 0);
        renderer!.SetText(width, height, textPixels);
    }

    private void DrawLabel(Media.DrawingContext dc, GlassLabel label, Media.Brush ink)
    {
        var box = label.Bounds;
        var typeface = new Media.Typeface(SettingsTheme.FontFamily, W.FontStyles.Normal, W.FontWeights.Medium, W.FontStretches.Normal);
        var text = new Media.FormattedText(label.Text, CultureInfo.CurrentUICulture, W.FlowDirection.LeftToRight,
            typeface, label.Size * dpi, ink, 1);
        double centerY = (box.Top + box.Height / 2) * dpi;
        double x = (box.Left + box.Width / 2) * dpi - text.Width / 2;
        var clip = label.Clip;
        dc.PushClip(new Media.RectangleGeometry(new W.Rect(clip.X * dpi, clip.Y * dpi, clip.Width * dpi, clip.Height * dpi)));
        if (label.Icon is { } glyph)
        {
            var iconFont = new Media.Typeface("Segoe Fluent Icons");
            var icon = new Media.FormattedText(glyph, CultureInfo.InvariantCulture, W.FlowDirection.LeftToRight,
                iconFont, 16 * dpi, ink, 1);
            double iconX = (box.Left + 14) * dpi;
            dc.DrawText(icon, new W.Point(iconX, centerY - icon.Height / 2));
            x = iconX + 26 * dpi;
        }
        dc.DrawText(text, new W.Point(x, centerY - text.Height / 2));
        dc.Pop();
    }

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
        delayedPresents = 0;
        image.Source = null;
        softwareImage = null;
        pixels = [];
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
        Stop();
        ReleaseImage();
        renderer?.Dispose();
        renderer = null;
    }
}
