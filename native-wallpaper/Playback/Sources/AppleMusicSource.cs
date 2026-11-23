using Windows.Media.Control;
using LyricWallpaper.Playback;

namespace LyricWallpaper.Playback.Sources;

internal static class AppleMusicSource
{
    public static async Task RunAsync(MusicOptions options, Action<PlayerSnapshot?> publish, Action<string> log,
        CancellationToken stop)
    {
        var interval = TimeSpan.FromSeconds(ServiceOptions.Seconds(options.ApplePollInterval, .05, 60, .2));
        double backoff = 1;
        bool unavailable = false;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(stop)
                    .WaitAsync(TimeSpan.FromSeconds(5), stop);
                while (!stop.IsCancellationRequested)
                {
                    var session = manager.GetSessions().FirstOrDefault(item =>
                        item.SourceAppUserModelId.Contains("applemusic", StringComparison.OrdinalIgnoreCase));
                    publish(session is null ? null : await ReadAsync(session, stop));
                    if (unavailable) log("Apple Music SMTC 已恢复。");
                    unavailable = false;
                    backoff = 1;
                    await Task.Delay(interval, stop);
                }
            }
            catch (Exception ex) when (!stop.IsCancellationRequested)
            {
                if (!unavailable) log("Apple Music SMTC 暂不可用：" + ex.Message);
                unavailable = true;
                await Task.Delay(TimeSpan.FromSeconds(backoff), stop);
                backoff = Math.Min(backoff * 2, 30);
            }
        }
    }

    private static async Task<PlayerSnapshot?> ReadAsync(GlobalSystemMediaTransportControlsSession session, CancellationToken stop)
    {
        var media = await session.TryGetMediaPropertiesAsync().AsTask(stop).WaitAsync(TimeSpan.FromSeconds(5), stop);
        var timeline = session.GetTimelineProperties();
        double duration = timeline.EndTime.TotalSeconds;
        if (string.IsNullOrWhiteSpace(media.Title) || duration <= 0) return null;
        bool playing = session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        double position = timeline.Position.TotalSeconds;
        if (playing) position = Math.Min(duration, position + Math.Max(0, (DateTimeOffset.UtcNow - timeline.LastUpdatedTime).TotalSeconds));
        string artist = (media.Artist ?? "").Split(" — ", 2, StringSplitOptions.None)[0].Trim();
        return new(media.Title.Trim(), artist, duration, position, playing);
    }
}
