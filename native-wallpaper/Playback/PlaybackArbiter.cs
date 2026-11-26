using System.Diagnostics;

namespace LyricWallpaper.Playback;

internal sealed class PlaybackArbiter(string[] priority, TimeSpan maxAge)
{
    private readonly Dictionary<string, Observation> observations = new();

    public void Update(string source, PlayerSnapshot? snapshot, long now)
    {
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.Song) || snapshot.Duration is <= 0 ||
            !double.IsFinite(snapshot.Position) || snapshot.Position < 0)
        {
            observations.Remove(source);
            return;
        }
        long transition = now;
        if (observations.TryGetValue(source, out var previous) && previous.Snapshot is { } old
            && old.Playing == snapshot.Playing && old.Song == snapshot.Song && old.Author == snapshot.Author
            && (old.Duration is not double before || snapshot.Duration is not double after ||
                Math.Round(before, 1) == Math.Round(after, 1)))
            transition = previous.Transition;
        int rank = Array.IndexOf(priority, source);
        observations[source] = new(source, snapshot, now, transition, rank < 0 ? 1 : rank);
    }

    public Observation? Pick(long now)
    {
        Observation? best = null;
        foreach (var candidate in observations.Values)
        {
            if (Stopwatch.GetElapsedTime(candidate.Observed, now) > maxAge) continue;
            if (best is null || candidate.Snapshot.Playing.CompareTo(best.Snapshot.Playing) > 0
                || (candidate.Snapshot.Playing == best.Snapshot.Playing
                    && (candidate.Transition > best.Transition
                        || (candidate.Transition == best.Transition && candidate.Priority < best.Priority))))
                best = candidate;
        }
        return best;
    }

    internal sealed record Observation(string Source, PlayerSnapshot Snapshot, long Observed, long Transition, int Priority);
}
