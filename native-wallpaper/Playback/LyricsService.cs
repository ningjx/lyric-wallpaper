using LyricWallpaper.Playback.Resolution;

namespace LyricWallpaper.Playback;

internal sealed class LyricsService : IDisposable
{
    private readonly object gate = new();
    private readonly object logGate = new();
    private readonly Task supervisor;
    private CancellationTokenSource? active;
    private bool disposed;
    private PlaybackSnapshot? latest;
    private string? error;
    public AutoResetEvent Updated { get; } = new(false);
    public PlaybackSnapshot? Latest => Volatile.Read(ref latest);
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

            void Publish(PlaybackSnapshot sample)
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
}
