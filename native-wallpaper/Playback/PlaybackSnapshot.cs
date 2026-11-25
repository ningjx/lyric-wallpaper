namespace LyricWallpaper.Playback;

internal sealed record PlaybackTrack(string Song, string Author, string Lyrics, string Translation);
internal sealed record PlaybackSnapshot(bool HasSong, bool Playing, double Position, PlaybackTrack Track, long ReceivedAt);

internal sealed record PlayerSnapshot(string Song, string Author, double? Duration, double Position, bool Playing);
