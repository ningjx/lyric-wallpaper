using System.Diagnostics;

internal sealed class PlaybackArbiter(string[] priority, TimeSpan maxAge)
{
    private readonly Dictionary<string, Observation> observations = new();

    public void Update(string source, PlayerSnapshot? snapshot, long now)
    {
        if (snapshot is not { Duration: > 0 } || string.IsNullOrWhiteSpace(snapshot.Song))
        {
            observations.Remove(source);
            return;
        }
        long transition = now;
        if (observations.TryGetValue(source, out var previous) && previous.Snapshot is { } old
            && old.Playing == snapshot.Playing && old.Song == snapshot.Song && old.Author == snapshot.Author
            && Math.Round(old.Duration, 1) == Math.Round(snapshot.Duration, 1))
            transition = previous.Transition;
        int rank = Array.IndexOf(priority, source);
        observations[source] = new(snapshot, now, transition, rank < 0 ? 1 : rank);
    }

    public PlayerSnapshot? Pick(long now)
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
        return best?.Snapshot;
    }

    private sealed record Observation(PlayerSnapshot Snapshot, long Observed, long Transition, int Priority);
}
