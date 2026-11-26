using System.Diagnostics;

namespace LyricWallpaper.Playback;

internal sealed record PlaybackTrack(string Song, string Author, string Lyrics, string Translation);
internal enum LyricActivity { Idle, Searching, Downloading, WaitingToRetry, Ready, Unavailable, Failed }

internal sealed record PlaybackSnapshot(string? Source, bool Playing, double Position, double? Duration,
    PlaybackTrack Track, long ReceivedAt, LyricActivity Lyrics, bool DetectionEnabled)
{
    public bool HasSong => Source is not null;
    public double CurrentPosition => Math.Max(0, Position + (Playing ? Stopwatch.GetElapsedTime(ReceivedAt).TotalSeconds : 0));
}

internal sealed record PlayerSnapshot(string Song, string Author, double? Duration, double Position, bool Playing);
