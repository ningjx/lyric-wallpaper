namespace LyricWallpaper.App;

internal sealed class RenderReport
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
