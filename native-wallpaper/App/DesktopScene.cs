using System.Diagnostics;
using System.Windows.Threading;
using LyricWallpaper.Platform.Windows;
using LyricWallpaper.Playback;
using LyricWallpaper.Rendering;
using LyricWallpaper.Scene;
using LyricWallpaper.Wallpaper;
using LyricWallpaper.Widgets;

namespace LyricWallpaper.App;

internal static class DesktopScene
{
    public static RenderReport Run(LaunchOptions options, bool measure, LyricsService? playback, WallpaperSettings appearance,
        WidgetCatalog catalog, WidgetDocument initialWidgets, bool startTest, Action<RenderControls>? ready,
        Action<PlaybackSnapshot?>? updateStatus, Action<WidgetEditorLayout>? updateEditor,
        Action<WidgetPreviewFrame[]>? updateDockPreviews, Action<WidgetHold>? openFromWidget,
        CancellationToken stop)
    {
        using var wallpaper = new WallpaperSession(appearance, options.Demo, options.Still, options.Fps, startTest);
        using var host = new DesktopHost(options.Preview);
        using var input = new WidgetDesktopInput(options.Preview);
        using var text = new TextAtlas();
        using var process = Process.GetCurrentProcess();
        using var wait = new DesktopWindow.MessageWait();
        var content = new WidgetScene(catalog);
        var dockPreviews = new WidgetDockPreviews(catalog);
        var feedback = new WidgetPressFeedback();
        var lyrics = new List<GlassCard>();
        var cards = new List<GlassCard>();
        WidgetLayout[] layouts = [];
        WidgetEditorLayout? lastLayout = null;
        WidgetDocument widgets = initialWidgets;
        DesktopTarget? desktop = null;
        D3D11Renderer? renderer = null;
        Guid? dragging = null, presentedFeedback = null;
        float presentedPress = 0;
        bool editing = false, showDockPreviews = false, lastAnimate = true, dirty = true, dirtyWidgets = true;
        var report = new RenderReport { Preview = options.Preview, Still = options.Still, FpsLimit = wallpaper.Fps };
        var intervals = measure ? new List<double>() : null;
        var clock = Stopwatch.StartNew();
        TimeSpan startCpu = default;
        LyricLine[]? lastLines = null;
        double measureFrom = -1, nextCheck = 0, nextFrame = 0, lastFocus = double.NaN, lastPresent = -1, lead = 0, clockTick = 0;
        string? capturePath = options.CapturePath;
        (bool HasSong, bool Playing, string Song)? lastStatus = null;
        var stopping = stop.CanBeCanceled ? stop.WaitHandle : null;

        WidgetDocument SetEditing(bool enabled)
        {
            if (editing == enabled) return widgets;
            input.Reset();
            dragging = null;
            editing = enabled;
            showDockPreviews = enabled;
            if (enabled) dockPreviews.Invalidate();
            lastLayout = null;
            dirty = true;
            return widgets;
        }

        void SetDragging(Guid? id)
        {
            dragging = id;
            if (id is { } widget) feedback.Begin(widget, clock.Elapsed.TotalSeconds);
        }

        void SetTestMode(bool enabled)
        {
            if (!wallpaper.SetTesting(enabled, clock.Elapsed.TotalSeconds)) return;
            dirty = true;
            lastPresent = -1;
            nextFrame = 0;
        }

        void ApplyAppearance(WallpaperSettings next)
        {
            var previousStyle = wallpaper.Style;
            var previousBackground = wallpaper.Background;
            double previousScale = wallpaper.Appearance.RenderScale;
            wallpaper.Apply(next, clock.Elapsed.TotalSeconds, scale => renderer?.ChangeRenderScale(scale),
                image => renderer?.ChangeBackground(image));
            var currentStyle = wallpaper.Style;
            if (!ReferenceEquals(previousBackground, wallpaper.Background) || previousScale != next.RenderScale ||
                previousStyle.Background != currentStyle.Background || previousStyle.Glass != currentStyle.Glass ||
                previousStyle.Lyrics.BehindGlass != currentStyle.Lyrics.BehindGlass)
                dockPreviews.Invalidate();
            if (renderer is not null)
            {
                lead = LyricLayer.LeadSeconds(renderer.OutputHeight, next);
                report.RenderWidth = renderer.Width;
                report.RenderHeight = renderer.Height;
            }
            report.FpsLimit = wallpaper.Fps;
            dirty = true;
            nextFrame = 0;
        }

        try
        {
            ready?.Invoke(new(Dispatcher.CurrentDispatcher, ApplyAppearance, SetTestMode, SetEditing,
                next => widgets = next, SetDragging, visible => showDockPreviews = editing && visible));
            while (true)
            {
                Application.DoEvents();
                double now = clock.Elapsed.TotalSeconds;
                if (stop.IsCancellationRequested || (measureFrom >= 0 && now - measureFrom >= options.Duration) ||
                    host.Window?.ClosedByUser == true) break;

                if (wallpaper.Rotate(now, image => renderer?.ChangeBackground(image)))
                {
                    dockPreviews.Invalidate();
                    dirty = true;
                }

                var sample = playback?.Latest;
                if (playback is not null)
                {
                    wallpaper.Update(sample, now);
                    (bool HasSong, bool Playing, string Song)? status = sample is null
                        ? null : (sample.HasSong, sample.Playing, sample.Track.Song);
                    if (status != lastStatus)
                    {
                        lastStatus = status;
                        updateStatus?.Invoke(sample);
                    }
                }

                if (now >= nextCheck || host.Window is { IsAlive: false })
                {
                    nextCheck = now + 1;
                    desktop = host.Probe();
                    if (desktop is { } target)
                    {
                        if (host.NeedsAttachment(target))
                        {
                            input.Reset();
                            renderer?.Dispose();
                            renderer = null;
                            var window = host.Attach(target);
                            var client = window.ClientSize;
                            renderer = new D3D11Renderer(window.Handle, client.Width, client.Height, wallpaper.Background, TextAtlas.Size,
                                target.Layer.Raised, wallpaper.Appearance.RenderScale);
                            text.Attach(renderer);
                            dockPreviews.Invalidate();
                            host.Reveal(target);
                            report.Width = client.Width;
                            report.Height = client.Height;
                            report.RenderWidth = renderer.Width;
                            report.RenderHeight = renderer.Height;
                            report.Adapter = renderer.Adapter;
                            report.Presentation = renderer.Presentation;
                            report.Layer = options.Preview ? "preview" : target.Layer.Raised ? "raised Progman" : "classic WorkerW";
                            report.Window = FormatHandle(window.Handle);
                            report.Parent = FormatHandle(target.Layer.Parent);
                            report.Icons = FormatHandle(target.Layer.Icons);
                            report.Attachments++;
                            lead = LyricLayer.LeadSeconds(client.Height, wallpaper.Appearance);
                            dirty = true;
                            lastPresent = -1;
                            nextFrame = 0;
                        }
                        if (!options.Preview)
                        {
                            if (host.MaintainPlacement(target)) report.BehindIconsChecks++;
                            else report.FailedLayerChecks++;
                        }
                    }
                    else
                    {
                        if (measureFrom < 0 && now > 8)
                            throw new InvalidOperationException("Explorer did not expose a wallpaper layer behind the desktop icons.");
                        renderer?.Dispose();
                        renderer = null;
                        input.Reset();
                        host.Detach();
                        report.FailedLayerChecks++;
                    }
                    now = clock.Elapsed.TotalSeconds;
                }

                double wake = measureFrom >= 0 ? Math.Min(nextCheck, measureFrom + options.Duration) : nextCheck;
                if (renderer is not null && desktop is { } surface && host.Window is { } attached)
                {
                    if (measureFrom < 0)
                    {
                        measureFrom = now;
                        startCpu = process.TotalProcessorTime;
                    }
                    var lyric = wallpaper.At(now, measureFrom, lead);
                    bool animate = System.Windows.SystemParameters.ClientAreaAnimation;
                    DateTime localTime = DateTime.Now;
                    dirtyWidgets |= content.Update(widgets, lyric.Idle, editing, localTime);
                    bool hasWidgets = content.Visible.Count != 0;
                    feedback.Animate(now - clockTick, now, dragging, !animate);
                    clockTick = now;
                    var feedbackWidget = feedback.WidgetId;
                    bool widgetAnimation = hasWidgets && feedback.IsAnimating;
                    bool animating = lyric.Moving || widgetAnimation;
                    bool needsFrame = dirty || dirtyWidgets || lyric.Focus != lastFocus ||
                        !ReferenceEquals(lastLines, lyric.Lines) || !lyric.Moving && lastPresent >= 0 ||
                        (hasWidgets && (feedback.Press != presentedPress || presentedFeedback != feedbackWidget || lastAnimate != animate));
                    if (needsFrame && (now >= nextFrame || !animating))
                    {
                        var viewport = new SceneViewport(renderer.Width, renderer.Height,
                            new(surface.Display.Location, new Size(renderer.OutputWidth, renderer.OutputHeight)));
                        text.BeginFrame();
                        lyrics.Clear();
                        cards.Clear();
                        LyricLayer.Collect(lyric, wallpaper.Appearance, viewport, text, lyrics);
                        layouts = WidgetLayer.Collect(content.Visible, viewport, wallpaper.Style.Glass, text,
                            feedbackWidget, feedback.Press, animate, editing, cards);
                        int previousBlurBuilds = renderer.BlurBuilds;
                        bool presented = renderer.Draw(new SceneFrame(wallpaper.Style, lyrics, cards), capturePath);
                        report.BlurBuilds += renderer.BlurBuilds - previousBlurBuilds;
                        if (presented)
                        {
                            report.Frames++;
                            var presentedAt = clock.Elapsed.TotalSeconds;
                            if (lyric.Moving && lastPresent >= 0 && intervals is { Count: < 36_000 })
                                intervals.Add((presentedAt - lastPresent) * 1000);
                            lastPresent = lyric.Moving ? presentedAt : -1;
                        }
                        else
                        {
                            report.OccludedPresents++;
                            lastPresent = -1;
                        }
                        capturePath = null;
                        lastFocus = lyric.Focus;
                        lastLines = lyric.Lines;
                        (presentedPress, presentedFeedback, lastAnimate) = (feedback.Press, feedbackWidget, animate);
                        dirty = dirtyWidgets = false;
                        double framePeriod = 1.0 / wallpaper.Fps;
                        nextFrame = (now - nextFrame > framePeriod ? now : nextFrame) + framePeriod;
                    }

                    wake = Math.Min(wake, lyric.Moving ? nextFrame : wallpaper.NextLyric(lyric, measureFrom));
                    wake = Math.Min(wake, content.Next(now, localTime));
                    if (hasWidgets) wake = Math.Min(wake, feedback.NextChange(now));
                    if (widgetAnimation) wake = Math.Min(wake, now < nextFrame ? nextFrame : now + 1.0 / 60);
                    if (editing)
                    {
                        var placement = new WidgetEditorLayout(surface.Display, surface.WorkArea, layouts, surface.Layer.Icons, attached.Handle);
                        if (lastLayout is not { } previous || previous.Display != placement.Display || previous.WorkArea != placement.WorkArea ||
                            previous.IconView != placement.IconView || previous.WallpaperView != placement.WallpaperView ||
                            !layouts.AsSpan().SequenceEqual(previous.Items))
                        {
                            lastLayout = placement;
                            updateEditor?.Invoke(placement);
                        }
                        if (showDockPreviews && updateDockPreviews is not null)
                        {
                            int previousBlurBuilds = renderer.BlurBuilds;
                            if (dockPreviews.Render(renderer, wallpaper.Style, text, localTime) is { } previews)
                                updateDockPreviews(previews);
                            report.BlurBuilds += renderer.BlurBuilds - previousBlurBuilds;
                            wake = Math.Min(wake, dockPreviews.Next(now, localTime));
                        }
                    }
                    else if (layouts.Length != 0 && openFromWidget is { } open)
                    {
                        if (input.Poll(attached, surface.Layer, layouts, now, open) is { } tapped)
                        {
                            feedback.Begin(tapped, clock.Elapsed.TotalSeconds);
                            wake = now;
                        }
                        wake = Math.Min(wake, input.Place(layouts, now));
                    }
                    else input.Reset();
                }
                wait.Until(Math.Min(wake, wallpaper.NextRotation) - clock.Elapsed.TotalSeconds, playback?.Updated, stopping);
            }

            report.ElapsedSeconds = clock.Elapsed.TotalSeconds - Math.Max(0, measureFrom);
            report.RenderScale = wallpaper.Appearance.RenderScale;
            report.FullSceneBlur = wallpaper.Appearance.FullSceneBlur;
            report.TestScroll = wallpaper.Testing;
            report.CpuMilliseconds = (process.TotalProcessorTime - startCpu).TotalMilliseconds;
            report.Songs = wallpaper.Songs;
            report.LoadedLyrics = wallpaper.LoadedLyrics;
            report.Pauses = wallpaper.Pauses;
            report.Seeks = wallpaper.Seeks;
            report.WorkerOnline = playback?.Latest is not null;
            report.WorkerDiagnostic = playback is { Latest: null } ? playback.Error : null;
            report.MovingIntervals = intervals?.Count ?? 0;
            if (intervals is { Count: > 0 })
            {
                intervals.Sort();
                report.MovingFrameIntervalP50Ms = intervals[(intervals.Count - 1) / 2];
                report.MovingFrameIntervalP95Ms = intervals[(int)Math.Ceiling(intervals.Count * .95) - 1];
            }
            return report;
        }
        finally { renderer?.Dispose(); }
    }

    private static string FormatHandle(nint value) => $"0x{value:X}";
}
