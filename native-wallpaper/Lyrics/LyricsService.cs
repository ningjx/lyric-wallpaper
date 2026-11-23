using System.Diagnostics;

internal sealed record WorkerTrack(string Song, string Author, string Lyrics, string Translation);
internal sealed record WorkerSample(bool HasSong, bool Playing, double Position, WorkerTrack Track, long ReceivedAt);

internal sealed class LyricsService : IDisposable
{
    private readonly object gate = new();
    private readonly object logGate = new();
    private readonly Task supervisor;
    private CancellationTokenSource? active;
    private bool disposed;
    private WorkerSample? latest;
    private string? error;
    public AutoResetEvent Updated { get; } = new(false);
    public WorkerSample? Latest => Volatile.Read(ref latest);
    public string? Error => Volatile.Read(ref error);

    public LyricsService(string configPath, string logDirectory)
    {
        supervisor = Task.Run(() => SuperviseAsync(configPath, Path.Combine(logDirectory, "native-worker.log")));
    }

    public void Restart()
    {
        lock (gate)
        {
            if (disposed) return;
            if (active is not null) _ = active.CancelAsync();
            Volatile.Write(ref latest, null);
            Volatile.Write(ref error, null);
            Updated.Set();
        }
    }

    private async Task SuperviseAsync(string configPath, string defaultLog)
    {
        while (true)
        {
            using var session = new CancellationTokenSource();
            lock (gate)
            {
                if (disposed) return;
                active = session;
            }
            var stop = session.Token;
            string logPath = defaultLog;
            try
            {
                var options = ServiceOptions.Load(configPath, Log);
                if (!string.IsNullOrWhiteSpace(options.Http.LogFile)) logPath = Path.GetFullPath(options.Http.LogFile);
                using var http = new LyricHttp(options.Netease, stop);
                var resolver = new LyricResolver(options, http, Log);
                var playback = new PlaybackSession(options, resolver, Publish, Log, stop);
                await playback.RunAsync(configPath);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception ex)
            {
                Log(ex.ToString());
                await Task.Delay(TimeSpan.FromSeconds(2), stop).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            finally
            {
                lock (gate) active = null;
            }

            void Publish(WorkerSample sample)
            {
                lock (gate)
                {
                    if (stop.IsCancellationRequested || !ReferenceEquals(active, session)) return;
                    Volatile.Write(ref latest, sample);
                    Updated.Set();
                }
            }

            void Log(string message)
            {
                if (stop.IsCancellationRequested) return;
                Volatile.Write(ref error, message);
                lock (logGate)
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                        File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
                }
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            if (active is not null) _ = active.CancelAsync();
        }
        supervisor.GetAwaiter().GetResult();
        Updated.Dispose();
    }

    private sealed class PlaybackSession(ServiceOptions options, LyricResolver resolver, Action<WorkerSample> publish,
        Action<string> log, CancellationToken stop)
    {
        private static readonly WorkerTrack EmptyTrack = new("", "", "", "");
        private readonly object gate = new();
        private readonly PlaybackArbiter arbiter = new(options.SourcePriority,
            TimeSpan.FromSeconds(ServiceOptions.Seconds(options.Music.SnapshotMaxAge, .05, 600, 2)));
        private readonly HashSet<Task> queries = new();
        private PlaybackQuery? query;
        private WorkerTrack track = EmptyTrack;
        private WorkerSample? last;

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

        private void Update(string source, PlayerSnapshot? snapshot)
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
            var selected = arbiter.Pick(now);
            if (selected is null || selected.Song != track.Song || selected.Author != track.Author)
            {
                if (query is not null) _ = query.Cancellation.CancelAsync();
                query = null;
                track = selected is null ? EmptyTrack : new(selected.Song, selected.Author, "", "");
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
            if (last is not null && ReferenceEquals(last.Track, track) && last.Position == position && last.Playing == playing) return;
            last = new(selected is not null, playing, position, track, now);
            publish(last);
        }

        private async Task ResolveAsync(PlaybackQuery lookup, PlayerSnapshot selected)
        {
            try
            {
                var result = await resolver.ResolveAsync(selected.Song, selected.Author, lookup.Cancellation.Token);
                lock (gate)
                {
                    if (result is null || stop.IsCancellationRequested || !ReferenceEquals(query, lookup)) return;
                    track = new(selected.Song, selected.Author, result.Lrc, result.TranslatedLyric);
                    Refresh();
                }
            }
            catch (OperationCanceledException) when (lookup.Cancellation.IsCancellationRequested) { }
            catch (Exception ex) { log($"歌词解析失败（{selected.Song} - {selected.Author}）：{ex.Message}"); }
            finally
            {
                lock (gate)
                {
                    if (ReferenceEquals(query, lookup)) query = null;
                    lookup.Cancellation.Dispose();
                }
            }
        }

        private sealed record PlaybackQuery(CancellationTokenSource Cancellation);
    }
}
