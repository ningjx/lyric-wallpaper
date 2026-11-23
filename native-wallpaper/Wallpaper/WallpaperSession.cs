using LyricWallpaper.Playback;
using LyricWallpaper.Scene;

namespace LyricWallpaper.Wallpaper;

internal sealed class WallpaperSession : IDisposable
{
    private readonly LyricTimeline timeline = new();
    private readonly bool demo, still;
    private readonly int? fpsOverride;
    private string? rotationFile;
    private double testStartedAt;

    public WallpaperSettings Appearance { get; private set; }
    public SceneStyle Style { get; private set; }
    public Bitmap Background { get; private set; }
    public double NextRotation { get; private set; }
    public bool Testing { get; private set; }
    public int Fps => fpsOverride ?? Appearance.Fps;
    public int Songs => timeline.Songs;
    public int LoadedLyrics => timeline.LoadedLyrics;
    public int Pauses => timeline.Pauses;
    public int Seeks => timeline.Seeks;

    public WallpaperSession(WallpaperSettings appearance, bool demo, bool still, int? fpsOverride, bool testing)
    {
        (this.demo, this.still, this.fpsOverride) = (demo, still, fpsOverride);
        Appearance = appearance;
        Style = appearance.ToSceneStyle();
        Testing = testing;
        try { Background = BackgroundImages.Load(appearance); }
        catch (Exception ex) when (appearance.Background.Length > 0 && BackgroundRotation.IsImageError(ex))
        {
            Background = BackgroundImages.Load(new WallpaperSettings());
        }
        NextRotation = appearance.BackgroundDirectory.Length == 0 ? double.PositiveInfinity : 0;
    }

    public void Update(PlaybackSnapshot? snapshot, double now) => timeline.Update(snapshot, now);

    public LyricFrame At(double now, double measuredFrom, double lead) => demo
        ? LyricLayer.DemoAt(now - measuredFrom, lead, still, Appearance.ScrollSpeed)
        : Testing ? LyricLayer.TestAt(now - testStartedAt, lead, Appearance) : timeline.At(now, lead, Appearance);

    public double NextLyric(LyricFrame frame, double measuredFrom) =>
        demo ? measuredFrom + frame.Next : Testing ? testStartedAt + frame.Next : frame.Next;

    public bool SetTesting(bool enabled, double now)
    {
        if (Testing == enabled) return false;
        Testing = enabled;
        testStartedAt = now;
        return true;
    }

    public bool Rotate(double now, Action<Bitmap> upload)
    {
        if (now < NextRotation) return false;
        NextRotation = now + Appearance.RotationSeconds;
        var next = BackgroundRotation.Pick(Appearance, rotationFile);
        if (next.Image is not { } image) return false;
        try { upload(image); }
        catch { image.Dispose(); throw; }
        ReplaceImage(image);
        rotationFile = next.File;
        return true;
    }

    public void Apply(WallpaperSettings updated, double now, Action<double> resize, Action<Bitmap> upload)
    {
        bool backgroundChanged = updated.Background != Appearance.Background ||
            updated.BackgroundDirectory != Appearance.BackgroundDirectory;
        bool rotationChanged = backgroundChanged || updated.RotationSeconds != Appearance.RotationSeconds;
        bool scaleChanged = updated.RenderScale != Appearance.RenderScale;
        var selected = backgroundChanged ? BackgroundRotation.Pick(updated, null) : default;
        Bitmap? image = selected.Image ?? (backgroundChanged ? BackgroundImages.Load(updated) : null);
        bool resized = false;
        try
        {
            try
            {
                if (scaleChanged)
                {
                    resize(updated.RenderScale);
                    resized = true;
                }
                if (image is not null) upload(image);
            }
            catch
            {
                if (resized) resize(Appearance.RenderScale);
                throw;
            }
            if (image is not null)
            {
                ReplaceImage(image);
                image = null;
            }
            Appearance = updated;
            Style = updated.ToSceneStyle();
            if (backgroundChanged) rotationFile = selected.File;
            if (rotationChanged) NextRotation = updated.BackgroundDirectory.Length == 0
                ? double.PositiveInfinity : now + updated.RotationSeconds;
        }
        finally { image?.Dispose(); }
    }

    private void ReplaceImage(Bitmap image)
    {
        Background.Dispose();
        Background = image;
    }

    public void Dispose() => Background.Dispose();
}
