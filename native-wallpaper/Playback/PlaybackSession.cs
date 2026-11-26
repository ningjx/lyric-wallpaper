using System.Diagnostics;
using LyricWallpaper.Playback.Resolution;
using LyricWallpaper.Playback.Sources;

namespace LyricWallpaper.Playback;

internal sealed class PlaybackSession(ServiceOptions options, LyricResolver resolver, Action<PlaybackSnapshot> publish,
    Action<string> log, CancellationToken stop)
{
    private static readonly PlaybackTrack EmptyTrack = new("", "", "", "");
    private readonly object gate = new();
    private readonly PlaybackArbiter arbiter = new(options.SourcePriority,
        TimeSpan.FromSeconds(ServiceOptions.Seconds(options.Music.SnapshotMaxAge, .05, 600, 2)));
    private readonly HashSet<Task> queries = new();
    private PlaybackQuery? query;
    private PlaybackTrack track = EmptyTrack;
    private LyricActivity lyrics;
    private PlaybackSnapshot? last;

    public async Task RunAsync(string configPath)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var sources = new List<Task> { ExpireSnapshotsAsync(run.Token) };
        if (options.Music.EnableApple)
            sources.Add(AppleMusicSource.RunAsync(options.Music, snapshot => Update("applemusic", snapshot), log, run.Token));
        if (options.Music.EnableNetease)
            sources.Add(new NeteaseSource(options.Music, configPath, log).RunAsync(snapshot => Update("netease", snapshot), run.Token));
        try
        {
            await await Task.WhenAny(sources);
        }
        finally
        {
            await run.CancelAsync();
            await Task.WhenAll(sources).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            Task[] remaining;
            lock (gate)
            {
                if (query is not null) _ = query.Cancellation.CancelAsync();
                remaining = queries.ToArray();
            }
            await Task.WhenAll(remaining);
        }
    }

    private async Task ExpireSnapshotsAsync(CancellationToken cancellation)
    {
        lock (gate) Refresh();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(.2));
        while (await timer.WaitForNextTickAsync(cancellation))
        {
            lock (gate) Refresh();
        }
    }

    internal void Update(string source, PlayerSnapshot? snapshot)
    {
        lock (gate)
        {
            if (stop.IsCancellationRequested) return;
            arbiter.Update(source, snapshot, Stopwatch.GetTimestamp());
            Refresh();
        }
    }

    private void Refresh()
    {
        long now = Stopwatch.GetTimestamp();
        var observation = arbiter.Pick(now);
        var selected = observation?.Snapshot;
        if (selected is null ? track != EmptyTrack : selected.Song != track.Song || selected.Author != track.Author)
        {
            if (query is not null) _ = query.Cancellation.CancelAsync();
            query = null;
            track = selected is null ? EmptyTrack : new(selected.Song, selected.Author, "", "");
            lyrics = selected is null ? LyricActivity.Idle : LyricActivity.Searching;
            if (selected is not null && !stop.IsCancellationRequested)
            {
                var lookup = new PlaybackQuery(CancellationTokenSource.CreateLinkedTokenSource(stop));
                query = lookup;
                var task = Task.Run(() => ResolveAsync(lookup, selected));
                queries.Add(task);
                _ = task.ContinueWith(done =>
                {
                    lock (gate) queries.Remove(done);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        double position = Math.Round(Math.Max(0, selected?.Position ?? 0), 3);
        bool playing = selected?.Playing ?? false;
        bool samePosition = last is not null && last.Source == observation?.Source && last.Position == position &&
            last.Playing == playing && last.Track.Song == track.Song && last.Track.Author == track.Author;
        if (samePosition && ReferenceEquals(last!.Track, track) && last.Duration == selected?.Duration && last.Lyrics == lyrics) return;
        // Changes to lyrics or duration must not restart the playback clock between player samples.
        long receivedAt = samePosition ? last!.ReceivedAt : observation?.Observed ?? now;
        last = new(observation?.Source, playing, position, selected?.Duration, track, receivedAt, lyrics,
            options.Music.EnableApple || options.Music.EnableNetease);
        publish(last);
    }

    private async Task ResolveAsync(PlaybackQuery lookup, PlayerSnapshot selected)
    {
        var cancellation = lookup.Cancellation.Token;
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                if (attempt > 0) SetActivity(LyricActivity.Searching);
                var result = await resolver.ResolveAsync(selected.Song, selected.Author, cancellation, PublishLyrics,
                    () => SetActivity(LyricActivity.Downloading));
                if (result is not null)
                {
                    PublishLyrics(result);
                    return;
                }
                if (attempt == 3)
                {
                    SetActivity(LyricActivity.Unavailable);
                    return;
                }
                SetActivity(LyricActivity.WaitingToRetry);
                await Task.Delay(resolver.MissRetryDelay * (1 << attempt), cancellation);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            SetActivity(LyricActivity.Failed);
            log($"歌词解析失败（{selected.Song} - {selected.Author}）：{ex.Message}");
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(query, lookup)) query = null;
                lookup.Cancellation.Dispose();
            }
        }

        void SetActivity(LyricActivity next)
        {
            lock (gate)
            {
                if (cancellation.IsCancellationRequested || !ReferenceEquals(query, lookup) ||
                    lyrics == next || lyrics == LyricActivity.Ready) return;
                lyrics = next;
                Refresh();
            }
        }

        void PublishLyrics(LyricsResult result)
        {
            lock (gate)
            {
                if (cancellation.IsCancellationRequested || !ReferenceEquals(query, lookup) ||
                    (track.Lyrics == result.Lrc && track.Translation == result.TranslatedLyric)) return;
                lyrics = LyricActivity.Ready;
                track = new(selected.Song, selected.Author, result.Lrc, result.TranslatedLyric);
                Refresh();
            }
        }
    }

    private sealed record PlaybackQuery(CancellationTokenSource Cancellation);
}
