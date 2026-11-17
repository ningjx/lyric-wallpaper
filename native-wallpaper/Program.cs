using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Windows.Threading;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [STAThread]
    private static int Main(string[] args)
    {
        string? reportPath = null;
        try
        {
            reportPath = Value(args, "--report");
            bool still = args.Contains("--still"), demo = still || args.Contains("--demo");
            var seconds = Value(args, "--seconds");
            var duration = seconds is not null ? double.Parse(seconds, CultureInfo.InvariantCulture)
                : demo ? 60 : double.PositiveInfinity;
            if (seconds is not null && (!double.IsFinite(duration) || duration <= 0))
                throw new ArgumentOutOfRangeException("--seconds");
            var fpsValue = Value(args, "--fps");
            int? fps = fpsValue is null ? null : int.Parse(fpsValue, CultureInfo.InvariantCulture);
            if (fps is < 1 or > 360) throw new ArgumentOutOfRangeException("--fps");
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            bool first = true;
            using var instance = demo ? null : new Mutex(true, @"Local\LyricWallpaper.Native", out first);
            if (instance is not null && !first)
                throw new InvalidOperationException("歌词壁纸已在运行，可从系统托盘退出。");
            var wallpaperProfile = Value(args, "--wallpaper-config");
            var storage = new DesktopSettings(Value(args, "--config"), wallpaperProfile);
            using var worker = demo ? null : new LyricsWorker(Value(args, "--python"), storage.ServicePath, storage.LogDirectory);
            var appearance = !demo || wallpaperProfile is not null ? storage.LoadWallpaper() : new WallpaperSettings();
            bool startTest = worker is not null && args.Contains("--test-scroll");
            Report Render(Action<RenderControls>? ready, Action<WorkerSample?>? updateStatus, CancellationToken stop) =>
                Run(duration, fps, args.Contains("--preview"), still, Value(args, "--capture"), reportPath is not null,
                    worker, appearance, startTest, ready, updateStatus, stop);
            var report = worker is null ? Render(null, null, default)
                : RunWithTray(storage, worker, appearance, startTest, args.Contains("--settings"), Render);
            WriteReport(reportPath, report);
            return report.Frames > 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            WriteReport(reportPath, new { Error = ex.ToString(), Win32Error = (ex as System.ComponentModel.Win32Exception)?.NativeErrorCode });
            if (reportPath is null)
                MessageBox.Show(ex.Message, "Native Lyrics Wallpaper", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private sealed record RenderControls(Dispatcher Dispatcher, Action<WallpaperSettings> ApplyAppearance, Action<bool> SetTestMode);

    private static Report RunWithTray(DesktopSettings storage, LyricsWorker worker, WallpaperSettings appearance,
        bool testing, bool openSettings, Func<Action<RenderControls>, Action<WorkerSample?>, CancellationToken, Report> render)
    {
        var ui = Dispatcher.CurrentDispatcher;
        DesktopTray? tray = null;
        var ready = new TaskCompletionSource<RenderControls>();
        var finished = new TaskCompletionSource<Report>();
        using var stop = new CancellationTokenSource();
        using var wait = new DesktopWindow.MessageWait();
        // The desktop window and D3D11 context share an STA that stays out of the
        // settings window's native move/resize loop and other modal UI.
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            try
            {
                finished.SetResult(render(ready.SetResult,
                    sample => ui.BeginInvoke(new Action(() => tray?.Update(sample))), stop.Token));
            }
            catch (Exception ex)
            {
                ready.TrySetException(ex);
                finished.SetException(ex);
            }
            finally { dispatcher.InvokeShutdown(); }
        }) { Name = "Desktop wallpaper renderer" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        try
        {
            var controls = ready.Task.GetAwaiter().GetResult();
            void OnRender(Action action)
            {
                if (finished.Task.IsCompleted) return;
                try { controls.Dispatcher.InvokeAsync(action, DispatcherPriority.Send).Task.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) when (finished.Task.IsCompleted) { }
            }
            using var desktopTray = new DesktopTray(storage, worker,
                () => appearance,
                updated =>
                {
                    OnRender(() => controls.ApplyAppearance(updated));
                    appearance = updated;
                },
                () => testing,
                enabled =>
                {
                    OnRender(() => controls.SetTestMode(enabled));
                    testing = enabled;
                });
            tray = desktopTray;
            tray.Update(worker.Latest);
            if (openSettings) tray.Open();
            var renderFinished = ((IAsyncResult)finished.Task).AsyncWaitHandle;
            while (!finished.Task.IsCompleted)
            {
                Application.DoEvents();
                if (tray.ExitRequested) break;
                wait.Until(double.PositiveInfinity, renderFinished);
            }
        }
        finally
        {
            tray = null;
            stop.Cancel();
            thread.Join();
        }
        return finished.Task.GetAwaiter().GetResult();
    }

    private static Report Run(double duration, int? fpsOverride, bool preview, bool still, string? capturePath, bool measure,
        LyricsWorker? worker, WallpaperSettings appearance, bool startTest, Action<RenderControls>? ready,
        Action<WorkerSample?>? updateStatus, CancellationToken stop)
    {
        Bitmap background;
        try { background = appearance.LoadBackground(); }
        catch (Exception ex) when (appearance.Background.Length > 0 && BackgroundRotation.IsImageError(ex))
        {
            background = new WallpaperSettings().LoadBackground();
        }
        int fps = fpsOverride ?? appearance.Fps;
        using var scene = new LyricScene();
        using var process = Process.GetCurrentProcess();
        using var wait = new DesktopWindow.MessageWait();
        var timeline = new LyricTimeline();
        var report = new Report { Preview = preview, Still = still, FpsLimit = fps };
        var intervals = measure ? new List<double>() : null;
        Span<Vector4> constants = stackalloc Vector4[D3D11Renderer.SceneVectors];
        DesktopWindow.Layer layer = default;
        DesktopWindow? window = null;
        D3D11Renderer? renderer = null;
        Size requestedSize = default;
        var clock = Stopwatch.StartNew();
        TimeSpan startCpu = default;
        LyricLine[]? lastLines = null;
        double measureFrom = -1, nextCheck = 0, nextFrame = 0, lastFocus = double.NaN, lastPresent = -1, lead = 0;
        double nextRotation = appearance.BackgroundDirectory.Length == 0 ? double.PositiveInfinity : 0;
        string? rotationFile = null;
        bool testing = startTest;
        double testStartedAt = clock.Elapsed.TotalSeconds;
        (bool HasSong, bool Playing, string Song)? lastStatus = null;
        var stopping = stop.CanBeCanceled ? stop.WaitHandle : null;

        void SetTestMode(bool enabled)
        {
            if (testing == enabled) return;
            testing = enabled;
            testStartedAt = clock.Elapsed.TotalSeconds;
            lastFocus = double.NaN;
            lastPresent = -1;
            nextFrame = 0;
        }

        void ApplyAppearance(WallpaperSettings updated)
        {
            bool backgroundChanged = updated.Background != appearance.Background ||
                updated.BackgroundDirectory != appearance.BackgroundDirectory;
            bool rotationChanged = backgroundChanged || updated.RotationSeconds != appearance.RotationSeconds;
            bool scaleChanged = updated.RenderScale != appearance.RenderScale;
            var selected = backgroundChanged ? BackgroundRotation.Pick(updated, null) : default;
            Bitmap? candidate = selected.Image ?? (backgroundChanged ? updated.LoadBackground() : null);
            bool resized = false;
            try
            {
                try
                {
                    if (renderer is not null)
                    {
                        if (scaleChanged)
                        {
                            renderer.ChangeRenderScale(updated.RenderScale);
                            resized = true;
                        }
                        if (candidate is not null) renderer.ChangeBackground(candidate);
                    }
                }
                catch
                {
                    if (resized) renderer!.ChangeRenderScale(appearance.RenderScale);
                    throw;
                }
                if (candidate is not null)
                {
                    background.Dispose();
                    background = candidate;
                    candidate = null;
                }
                appearance = updated;
                if (backgroundChanged) rotationFile = selected.File;
                if (rotationChanged)
                    nextRotation = updated.BackgroundDirectory.Length == 0
                        ? double.PositiveInfinity : clock.Elapsed.TotalSeconds + updated.RotationSeconds;
                fps = fpsOverride ?? updated.Fps;
                if (renderer is not null)
                {
                    lead = LyricScene.LeadSeconds(renderer.OutputHeight, updated);
                    report.RenderWidth = renderer.Width;
                    report.RenderHeight = renderer.Height;
                }
                report.FpsLimit = fps;
                lastFocus = double.NaN;
                nextFrame = 0;
            }
            finally { candidate?.Dispose(); }
        }

        try
        {
            ready?.Invoke(new(Dispatcher.CurrentDispatcher, ApplyAppearance, SetTestMode));
            while (true)
            {
                Application.DoEvents();
                var now = clock.Elapsed.TotalSeconds;
                if (stop.IsCancellationRequested || (measureFrom >= 0 && now - measureFrom >= duration) || window?.ClosedByUser == true)
                    break;

                if (now >= nextRotation)
                {
                    nextRotation = now + appearance.RotationSeconds;
                    var next = BackgroundRotation.Pick(appearance, rotationFile);
                    if (next.Image is { } image)
                    {
                        try { renderer?.ChangeBackground(image); }
                        catch { image.Dispose(); throw; }
                        background.Dispose();
                        background = image;
                        rotationFile = next.File;
                        lastFocus = double.NaN;
                    }
                }

                var sample = worker?.Latest;
                if (worker is not null)
                {
                    timeline.Update(sample, now);
                    (bool HasSong, bool Playing, string Song)? status = sample is null
                        ? null : (sample.HasSong, sample.Playing, sample.Track.Song);
                    if (status != lastStatus)
                    {
                        lastStatus = status;
                        updateStatus?.Invoke(sample);
                    }
                }

                if (now >= nextCheck || window is { IsAlive: false })
                {
                    nextCheck = now + 1;
                    var currentLayer = preview ? new DesktopWindow.Layer() : DesktopWindow.FindLayer();
                    var size = Screen.PrimaryScreen?.Bounds.Size ?? throw new InvalidOperationException("No primary display");
                    if (currentLayer is { } found)
                    {
                        if (window is not { IsAlive: true } || found != layer || size != requestedSize)
                        {
                            renderer?.Dispose();
                            renderer = null;
                            window?.Dispose();
                            window = new DesktopWindow(found, preview);
                            window.Position(found, size, false);
                            var client = window.ClientSize;
                            scene.InvalidateText();
                            renderer = new D3D11Renderer(window.Handle, client.Width, client.Height, background, scene.AtlasSize,
                                found.Raised, appearance.RenderScale);
                            layer = found;
                            requestedSize = size;
                            window.Position(layer, size, true);
                            report.Width = client.Width;
                            report.Height = client.Height;
                            report.RenderWidth = renderer.Width;
                            report.RenderHeight = renderer.Height;
                            report.Adapter = renderer.Adapter;
                            report.Presentation = renderer.Presentation;
                            report.Layer = preview ? "preview" : found.Raised ? "raised Progman" : "classic WorkerW";
                            report.Window = FormatHandle(window.Handle);
                            report.Parent = FormatHandle(found.Parent);
                            report.Icons = FormatHandle(found.Icons);
                            report.Attachments++;
                            lead = LyricScene.LeadSeconds(client.Height, appearance);
                            lastFocus = double.NaN;
                            lastPresent = -1;
                            nextFrame = 0;
                        }
                        if (!preview)
                        {
                            if (!window.IsBehindIcons(layer) || !window.IsShown)
                                window.Position(layer, size, true);
                            if (window.IsBehindIcons(layer) && window.IsShown) report.BehindIconsChecks++;
                            else report.FailedLayerChecks++;
                        }
                    }
                    else
                    {
                        if (measureFrom < 0 && now > 8)
                            throw new InvalidOperationException("Explorer did not expose a wallpaper layer behind the desktop icons.");
                        renderer?.Dispose();
                        renderer = null;
                        window?.Dispose();
                        window = null;
                        report.FailedLayerChecks++;
                    }
                    now = clock.Elapsed.TotalSeconds;
                }

                var wake = measureFrom >= 0 ? Math.Min(nextCheck, measureFrom + duration) : nextCheck;
                if (renderer is not null)
                {
                    if (measureFrom < 0)
                    {
                        measureFrom = now;
                        startCpu = process.TotalProcessorTime;
                    }
                    var state = worker is null
                        ? LyricScene.DemoAt(now - measureFrom, lead, still, appearance.ScrollSpeed)
                        : testing ? LyricScene.TestAt(now - testStartedAt, lead, appearance) : timeline.At(now, lead, appearance);
                    if (state.Focus != lastFocus || !ReferenceEquals(lastLines, state.Lines) || !state.Moving && lastPresent >= 0)
                    {
                        if (now >= nextFrame || !state.Moving)
                        {
                            var count = scene.Build(constants, renderer, state, appearance);
                            int previousBlurBuilds = renderer.BlurBuilds;
                            bool presented = renderer.Draw(constants, count, appearance, capturePath);
                            report.BlurBuilds += renderer.BlurBuilds - previousBlurBuilds;
                            if (presented)
                            {
                                report.Frames++;
                                var presentedAt = clock.Elapsed.TotalSeconds;
                                if (state.Moving && lastPresent >= 0 && intervals is { Count: < 36_000 })
                                    intervals.Add((presentedAt - lastPresent) * 1000);
                                lastPresent = state.Moving ? presentedAt : -1;
                            }
                            else
                            {
                                report.OccludedPresents++;
                                lastPresent = -1;
                            }
                            capturePath = null;
                            lastFocus = state.Focus;
                            lastLines = state.Lines;
                            var framePeriod = 1.0 / fps;
                            nextFrame = (now - nextFrame > framePeriod ? now : nextFrame) + framePeriod;
                        }
                    }
                    double wakeForLine = worker is null ? measureFrom + state.Next : testing ? testStartedAt + state.Next : state.Next;
                    wake = Math.Min(wake, state.Moving ? nextFrame : wakeForLine);
                }
                wait.Until(Math.Min(wake, nextRotation) - clock.Elapsed.TotalSeconds, worker?.Updated, stopping);
            }

            report.ElapsedSeconds = clock.Elapsed.TotalSeconds - Math.Max(0, measureFrom);
            report.RenderScale = appearance.RenderScale;
            report.FullSceneBlur = appearance.FullSceneBlur;
            report.TestScroll = testing;
            report.CpuMilliseconds = (process.TotalProcessorTime - startCpu).TotalMilliseconds;
            report.Songs = timeline.Songs;
            report.LoadedLyrics = timeline.LoadedLyrics;
            report.Pauses = timeline.Pauses;
            report.Seeks = timeline.Seeks;
            report.WorkerOnline = worker?.Latest is not null;
            report.WorkerDiagnostic = worker is { Latest: null } ? worker.Error : null;
            report.MovingIntervals = intervals?.Count ?? 0;
            if (intervals is { Count: > 0 })
            {
                intervals.Sort();
                report.MovingFrameIntervalP50Ms = intervals[(intervals.Count - 1) / 2];
                report.MovingFrameIntervalP95Ms = intervals[(int)Math.Ceiling(intervals.Count * .95) - 1];
            }
            return report;
        }
        finally
        {
            renderer?.Dispose();
            window?.Dispose();
            background.Dispose();
        }
    }

    private static string FormatHandle(nint value) => $"0x{value:X}";

    private static string? Value(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index < 0 ? null : index + 1 < args.Length ? args[index + 1] :
            throw new ArgumentException($"Missing value for {name}");
    }

    private static void WriteReport(string? path, object report)
    {
        if (path is not null) File.WriteAllText(path, JsonSerializer.Serialize(report, JsonOptions));
    }

    private sealed class Report
    {
        public bool Preview { get; init; }
        public bool Still { get; init; }
        public int FpsLimit { get; set; }
        public string? Adapter { get; set; }
        public string? Presentation { get; set; }
        public string? Layer { get; set; }
        public string? Window { get; set; }
        public string? Parent { get; set; }
        public string? Icons { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int RenderWidth { get; set; }
        public int RenderHeight { get; set; }
        public double RenderScale { get; set; }
        public bool FullSceneBlur { get; set; }
        public bool TestScroll { get; set; }
        public int BlurBuilds { get; set; }
        public int Attachments { get; set; }
        public int BehindIconsChecks { get; set; }
        public int FailedLayerChecks { get; set; }
        public int Frames { get; set; }
        public int OccludedPresents { get; set; }
        public double ElapsedSeconds { get; set; }
        public double CpuMilliseconds { get; set; }
        public int MovingIntervals { get; set; }
        public double? MovingFrameIntervalP50Ms { get; set; }
        public double? MovingFrameIntervalP95Ms { get; set; }
        public int Songs { get; set; }
        public int LoadedLyrics { get; set; }
        public int Pauses { get; set; }
        public int Seeks { get; set; }
        public bool WorkerOnline { get; set; }
        public string? WorkerDiagnostic { get; set; }
    }
}
