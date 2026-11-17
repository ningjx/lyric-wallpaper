using System.Diagnostics;
using System.Text;
using System.Text.Json;

internal sealed record WorkerTrack(string Song, string Author, string Lyrics, string Translation);
internal sealed record WorkerSample(bool HasSong, bool Playing, double Position, WorkerTrack Track, long ReceivedAt);

internal sealed class LyricsWorker : IDisposable
{
    private static readonly JsonSerializerOptions Protocol = new(JsonSerializerDefaults.Web);
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task supervisor;
    private Process? process;
    private WorkerSample? latest;
    private string? error;
    private int restartVersion;
    public AutoResetEvent Updated { get; } = new(false);
    public WorkerSample? Latest => Volatile.Read(ref latest);
    public string? Error => Volatile.Read(ref error);

    public LyricsWorker(string? python, string? config, string? logDirectory = null)
    {
        var start = CreateStartInfo(python, config);
        supervisor = Task.Run(() => Supervise(start, logDirectory, shutdown.Token));
    }

    public void Restart()
    {
        Interlocked.Increment(ref restartVersion);
        Volatile.Write(ref latest, null);
        CloseInput(Volatile.Read(ref process));
        Updated.Set();
    }

    private static void CloseInput(Process? child)
    {
        try { child?.StandardInput.Close(); }
        catch (IOException) { }
        catch (InvalidOperationException) { }
    }

    private static void Log(string? directory, string message)
    {
        if (directory is null) return;
        try
        {
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "native-worker.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private async Task Supervise(ProcessStartInfo start, string? logDirectory, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            int version = Volatile.Read(ref restartVersion);
            try
            {
                using var child = new Process { StartInfo = start };
                child.ErrorDataReceived += (_, message) =>
                {
                    if (!string.IsNullOrWhiteSpace(message.Data))
                    {
                        Volatile.Write(ref error, message.Data);
                        Log(logDirectory, message.Data);
                    }
                };
                if (!child.Start()) throw new InvalidOperationException("无法启动歌词采集进程。");
                Volatile.Write(ref process, child);
                using var stop = cancellation.Register(() => CloseInput(child));
                if (version != Volatile.Read(ref restartVersion)) CloseInput(child);
                try
                {
                    child.BeginErrorReadLine();
                    await ReadSnapshots(child.StandardOutput, version, cancellation);
                }
                finally
                {
                    if (!child.WaitForExit(2_000))
                    {
                        Terminate(child);
                        child.WaitForExit();
                    }
                    Volatile.Write(ref process, null);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException
                                       or System.ComponentModel.Win32Exception)
            {
                Volatile.Write(ref error, ex.Message);
                Log(logDirectory, ex.ToString());
            }

            Volatile.Write(ref latest, null);
            Updated.Set();
            if (cancellation.IsCancellationRequested) break;
            if (version == Volatile.Read(ref restartVersion))
                await Task.Delay(2_000, cancellation).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private async Task ReadSnapshots(StreamReader input, int version, CancellationToken cancellation)
    {
        WorkerTrack track = new("", "", "", "");
        while (await input.ReadLineAsync(cancellation) is { } line)
        {
            if (version != Volatile.Read(ref restartVersion)) break;
            var message = JsonSerializer.Deserialize<Message>(line, Protocol)
                ?? throw new JsonException("歌词采集进程返回了空消息。");
            if (message.Song is not null)
                track = new WorkerTrack(message.Song, message.Author ?? "", message.Lyrics ?? "", message.Translation ?? "");
            Volatile.Write(ref latest, new WorkerSample(message.HasSong, message.Playing, message.Position,
                track, Stopwatch.GetTimestamp()));
            Updated.Set();
        }
    }

    private static ProcessStartInfo CreateStartInfo(string? python, string? config)
    {
        var start = new ProcessStartInfo
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = AppContext.BaseDirectory
        };
        string packaged = Path.Combine(AppContext.BaseDirectory, "worker", "LyricWorker.exe");
        if (python is null && File.Exists(packaged))
        {
            start.FileName = packaged;
        }
        else
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "server", "worker.py")))
                directory = directory.Parent;
            if (directory is null)
                throw new FileNotFoundException("找不到随程序发布的歌词采集组件。", packaged);
            start.FileName = python ?? "python";
            start.WorkingDirectory = directory.FullName;
            start.ArgumentList.Add("-m");
            start.ArgumentList.Add("server.worker");
        }
        if (config is not null)
        {
            start.ArgumentList.Add("--config");
            start.ArgumentList.Add(Path.GetFullPath(config));
        }
        return start;
    }

    private static void Terminate(Process? child)
    {
        try { child?.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public void Dispose()
    {
        shutdown.Cancel();
        if (!supervisor.Wait(3_000))
        {
            Terminate(Volatile.Read(ref process));
            if (!supervisor.Wait(2_000)) return;
        }
        Updated.Dispose();
        shutdown.Dispose();
    }

    private sealed record Message(bool HasSong, bool Playing, double Position, string? Song,
        string? Author, string? Lyrics, string? Translation);
}
