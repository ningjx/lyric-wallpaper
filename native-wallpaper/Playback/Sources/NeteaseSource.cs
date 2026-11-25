using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using LyricWallpaper.Playback;
using LyricWallpaper.Playback.Resolution;
using Windows.Media.Control;

namespace LyricWallpaper.Playback.Sources;

internal sealed class NeteaseSource(MusicOptions options, string configPath, Action<string> log)
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan AttachInterval = TimeSpan.FromSeconds(1);
    private const string ProcessName = "cloudmusic", ModuleName = "cloudmusic.dll", OffsetFile = "offsets_config.json";
    private readonly string offsetPath = Path.Combine(Path.GetDirectoryName(configPath)!, OffsetFile);
    private MediaTrack? mediaTrack;

    public async Task RunAsync(Action<PlayerSnapshot?> publish, CancellationToken stop)
    {
        var interval = TimeSpan.FromSeconds(ServiceOptions.Seconds(options.NeteasePollInterval, .05, 60, .2));
        using var mediaLifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var media = WatchMediaAsync(interval, mediaLifetime.Token);
        try { await WatchMemoryAsync(interval, publish, stop); }
        finally
        {
            await mediaLifetime.CancelAsync();
            await media.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private async Task WatchMemoryAsync(TimeSpan interval, Action<PlayerSnapshot?> publish, CancellationToken stop)
    {
        var offsets = LoadOffsets();
        double probeRetry = 1;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var target = FindPlayer();
                if (target is not null)
                {
                    string version = target.Version;
                    if (!offsets.TryGetValue(version, out var known) || !target.TryReadPosition(known, out _))
                    {
                        bool? playing = Volatile.Read(ref mediaTrack)?.Playing;
                        if (playing != false && await ProbeProgressAsync(target, stop) is { } discovered)
                        {
                            offsets[version] = discovered;
                            known = discovered;
                            try { LyricCache.WriteJson(offsetPath, offsets); }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            {
                                log("无法保存网易云偏移缓存：" + ex.Message);
                            }
                        }
                        else
                        {
                            publish(null);
                            // Older clients without SMTC may stay paused indefinitely.
                            double delay = playing is null ? probeRetry : AttachInterval.TotalSeconds;
                            probeRetry = playing is null ? Math.Min(probeRetry * 2, 10) : 1;
                            await Task.Delay(TimeSpan.FromSeconds(delay), stop);
                            continue;
                        }
                    }
                    probeRetry = 1;
                    await PollAsync(target, known, interval, publish, stop);
                }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                log("网易云采集将重试：" + ex.Message);
            }
            publish(null);
            await Task.Delay(AttachInterval, stop);
        }
    }

    private async Task WatchMediaAsync(TimeSpan interval, CancellationToken stop)
    {
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
                        string.Equals(item.SourceAppUserModelId, ProcessName + ".exe", StringComparison.OrdinalIgnoreCase));
                    MediaTrack? track = null;
                    if (session is not null)
                    {
                        var properties = await session.TryGetMediaPropertiesAsync().AsTask(stop)
                            .WaitAsync(TimeSpan.FromSeconds(2), stop);
                        var status = session.GetPlaybackInfo().PlaybackStatus;
                        if (!string.IsNullOrWhiteSpace(properties?.Title) &&
                            status != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed)
                        {
                            bool? playing = status switch
                            {
                                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => true,
                                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused or
                                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => false,
                                _ => null
                            };
                            var timeline = session.GetTimelineProperties();
                            double duration = (timeline.EndTime - timeline.StartTime).TotalSeconds;
                            track = new(properties.Title.Trim(), properties.Artist?.Trim() ?? "", playing,
                                duration > 0 ? duration : null);
                        }
                    }
                    Volatile.Write(ref mediaTrack, track);
                    unavailable = false;
                    await Task.Delay(interval, stop);
                }
            }
            catch (Exception ex) when (!stop.IsCancellationRequested)
            {
                Volatile.Write(ref mediaTrack, null);
                if (!unavailable) log("网易云 SMTC 暂不可用：" + ex.Message);
                unavailable = true;
                await Task.Delay(TimeSpan.FromSeconds(5), stop);
            }
        }
    }

    private async Task PollAsync(PlayerMemory target, PlaybackOffsets offsets, TimeSpan interval,
        Action<PlayerSnapshot?> publish, CancellationToken stop)
    {
        (string Song, string Artist) windowTrack = ("", ""), previousTrack = ("", "");
        double previousPosition = 0, positionSample = 0;
        long titleCheckedAt = 0, positionCheckedAt = 0;
        bool moving = false;
        int stationary = 0;
        double titleRecheck = ServiceOptions.Seconds(options.TitleRecheckInterval, .05, .5, .5);
        while (!stop.IsCancellationRequested && target.TryReadPosition(offsets, out double position))
        {
            long now = Stopwatch.GetTimestamp();
            bool restarted = position < 1 && previousPosition - position > 1;
            var media = Volatile.Read(ref mediaTrack);
            if (media is null && (restarted || titleCheckedAt == 0 ||
                Stopwatch.GetElapsedTime(titleCheckedAt, now).TotalSeconds >= titleRecheck))
            {
                windowTrack = ReadTitle(target.Id);
                titleCheckedAt = now;
            }
            if (media is not null) titleCheckedAt = 0;
            var track = media is null ? windowTrack : (media.Song, media.Artist);

            double elapsed = Stopwatch.GetElapsedTime(positionCheckedAt, now).TotalSeconds;
            if (positionCheckedAt == 0 || elapsed >= .35)
            {
                if (positionCheckedAt != 0)
                {
                    double delta = position - positionSample;
                    if (Math.Abs(delta) > elapsed * 2 + .25) stationary = 0;
                    else if (delta > elapsed * .15) { moving = true; stationary = 0; }
                    else if (++stationary >= 2) moving = false;
                }
                positionSample = position;
                positionCheckedAt = now;
            }

            // Memory and SMTC update independently when the player starts a new track.
            // Give the title one poll to catch up before pairing a reset with the previous lyrics.
            if (!restarted || previousTrack != track)
            {
                double? duration = media?.Duration is double end && position <= end + 1 ? end : null;
                publish(track.Song.Length == 0 ? null : new(track.Song, track.Artist, duration, position, media?.Playing ?? moving));
                previousTrack = track;
            }
            previousPosition = position;
            await Task.Delay(interval, stop);
        }
    }

    private async Task<PlaybackOffsets?> ProbeProgressAsync(PlayerMemory target, CancellationToken stop)
    {
        log($"正在探测网易云 {target.Version} 的播放进度。");
        var startingTrack = Volatile.Read(ref mediaTrack);
        var before = target.ReadImage(stop);
        long first = Stopwatch.GetTimestamp();
        await Task.Delay(ProbeInterval, stop);
        if (target.HasExited) return null;
        var after = target.ReadImage(stop);
        long second = Stopwatch.GetTimestamp();
        await Task.Delay(ProbeInterval, stop);
        if (target.HasExited) return null;
        var latestTrack = Volatile.Read(ref mediaTrack);
        if (startingTrack is not null && latestTrack is not null &&
            (startingTrack.Song, startingTrack.Artist) != (latestTrack.Song, latestTrack.Artist)) return null;
        return FindProgressOffset(before, after, Stopwatch.GetElapsedTime(first, second).TotalSeconds,
            Stopwatch.GetElapsedTime(second).TotalSeconds,
            offsets => target.TryReadPosition(offsets, out double position) ? position : null);
    }

    private Dictionary<string, PlaybackOffsets> LoadOffsets()
    {
        var values = new Dictionary<string, PlaybackOffsets>
        {
            ["3.1.28.8527"] = new(0x1D808F8, 0x1DE1038, 0x1D80900),
            ["3.1.44.9062"] = new(0x2196268, 0, 0x2196270)
        };
        string legacyRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LyricWallpaper", "worker");
        try
        {
            if (Directory.Exists(legacyRoot))
                foreach (var directory in Directory.EnumerateDirectories(legacyRoot))
                    Merge(Path.Combine(directory, "_internal", "server", OffsetFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "server", OffsetFile);
            if (!File.Exists(path)) continue;
            Merge(path);
            break;
        }
        Merge(offsetPath);
        return values;

        void Merge(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                var offsets = JsonSerializer.Deserialize<Dictionary<string, PlaybackOffsets>>(File.ReadAllText(path), ServiceOptions.Json);
                if (offsets is null) return;
                foreach (var (version, entry) in offsets)
                    if (entry.Progress > 0 && entry.Rate > 0) values[version] = entry;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
    }

    internal static PlaybackOffsets? FindProgressOffset(byte[] before, byte[] after,
        double firstInterval, double secondInterval, Func<PlaybackOffsets, double?> readPosition)
    {
        var previous = MemoryMarshal.Cast<byte, double>(before);
        var current = MemoryMarshal.Cast<byte, double>(after);
        int length = Math.Min(previous.Length, current.Length);
        PlaybackOffsets? best = null;
        double bestError = double.PositiveInfinity;
        for (int i = 1; i + 1 < length; i++)
        {
            double position = previous[i], rate = current[i + 1];
            if (!(position is >= 0 and <= 86400) || !(rate is >= .5 and <= 2) ||
                !(Math.Abs(rate - previous[i + 1]) < .02)) continue;
            double firstError = Math.Abs(current[i] - position - firstInterval * rate);
            if (!(firstError <= .4) || firstError >= bestError) continue;
            var offsets = new PlaybackOffsets(i * sizeof(double), 0, (i + 1) * sizeof(double));
            if (readPosition(offsets) is not double last) continue;
            double secondError = Math.Abs(last - current[i] - secondInterval * rate);
            double error = firstError + secondError;
            if (!(secondError <= .4) || error >= bestError) continue;
            (best, bestError) = (offsets, error);
        }
        return best;
    }

    internal static (string Song, string Artist)? ParseTitle(string value)
    {
        string title = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (title.Contains("mediaplayer smtc window", StringComparison.OrdinalIgnoreCase)) return null;
        string[] parts = title.Split(" - ", 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0 || Guid.TryParseExact(parts[1], "B", out _)) return null;
        return (parts[0], parts[1]);
    }

    private static (string, string) ReadTitle(int player)
    {
        (string Song, string Artist) best = ("", "");
        bool bestVisible = false;
        int bestLength = -1;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint processId);
            if (processId != player) return true;
            int length = GetWindowTextLength(window);
            if (length <= 0) return true;
            var buffer = new StringBuilder(length + 1);
            GetWindowText(window, buffer, buffer.Capacity);
            string title = buffer.ToString();
            if (title.Contains("桌面歌词") || title.Contains("迷你播放器") || title.Contains("GDI+")) return true;
            bool visible = IsWindowVisible(window);
            if ((!bestVisible || visible) && (visible != bestVisible || length > bestLength) && ParseTitle(title) is { } track)
                (best, bestVisible, bestLength) = (track, visible, length);
            return true;
        }, 0);
        return best;
    }

    private static PlayerMemory? FindPlayer()
    {
        var processes = Process.GetProcessesByName(ProcessName);
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    var module = process.Modules.Cast<ProcessModule>().FirstOrDefault(item =>
                        item.ModuleName.Equals(ModuleName, StringComparison.OrdinalIgnoreCase));
                    if (module is null || module.ModuleMemorySize <= 0) continue;
                    var handle = OpenProcess(0x0410, false, process.Id); // PROCESS_QUERY_INFORMATION | PROCESS_VM_READ.
                    if (handle.IsInvalid) { handle.Dispose(); continue; }
                    string? executableVersion = null;
                    try
                    {
                        var version = process.MainModule?.FileVersionInfo;
                        if (version is not null) executableVersion = $"{version.FileMajorPart}.{version.FileMinorPart}.{version.FileBuildPart}.{version.FilePrivatePart}";
                    }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
                    return new PlayerMemory(process.Id, handle, module.BaseAddress, module.ModuleMemorySize, executableVersion);
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
            }
            return null;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private sealed record MediaTrack(string Song, string Artist, bool? Playing, double? Duration);

    // Keep the legacy duration field when reading and writing existing offset files.
    internal readonly record struct PlaybackOffsets(int Progress, int Duration, int Rate);

    private sealed class PlayerMemory(int id, SafeProcessHandle handle, nint address, int length, string? version) : IDisposable
    {
        public int Id => id;
        public bool HasExited => !GetExitCodeProcess(handle, out uint code) || code != 259;
        public string Version => version ?? ReadModuleVersion();

        private string ReadModuleVersion()
        {
            Span<byte> value = stackalloc byte[sizeof(int)];
            if (Read(0x3C, value) && Read(BitConverter.ToInt32(value) + 8, value))
                return $"stamp_{BitConverter.ToUInt32(value):X8}_{length:x}";
            return $"size_{length:x}";
        }

        public bool TryReadPosition(PlaybackOffsets offsets, out double position)
        {
            position = 0;
            Span<byte> positionAndRate = stackalloc byte[16];
            Span<byte> rateBytes = positionAndRate[8..];
            if (offsets.Rate == offsets.Progress + 8)
            {
                if (!Read(offsets.Progress, positionAndRate)) return false;
            }
            else if (!Read(offsets.Progress, positionAndRate[..8]) || !Read(offsets.Rate, rateBytes)) return false;
            position = BitConverter.ToDouble(positionAndRate);
            double rate = Math.Abs(BitConverter.ToDouble(rateBytes));
            return position is >= 0 and <= 86400 && (rate < .01 || rate is >= .5 and <= 2);
        }

        public byte[] ReadImage(CancellationToken stop)
        {
            var image = new byte[length];
            if (Read(0, image)) return image;
            const int block = 1024 * 1024, page = 4096;
            for (int offset = 0; offset < length; offset += block)
            {
                stop.ThrowIfCancellationRequested();
                int count = Math.Min(block, length - offset);
                if (Read(offset, image.AsSpan(offset, count))) continue;
                for (int pos = offset; pos < offset + count; pos += page)
                {
                    var buffer = image.AsSpan(pos, Math.Min(page, length - pos));
                    if (!Read(pos, buffer)) buffer.Clear();
                }
            }
            return image;
        }

        private unsafe bool Read(int offset, Span<byte> buffer)
        {
            if (offset < 0 || offset > length - buffer.Length) return false;
            fixed (byte* data = buffer)
                return ReadProcessMemory(handle, address + offset, data, (nuint)buffer.Length, out nuint read) &&
                    read == (nuint)buffer.Length;
        }

        public void Dispose() => handle.Dispose();
    }

    private delegate bool EnumWindow(nint window, nint state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, nint state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint window);
    [DllImport("kernel32.dll")] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int process);
    [DllImport("kernel32.dll")] private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
    [DllImport("kernel32.dll")] private static extern unsafe bool ReadProcessMemory(SafeProcessHandle process, nint address, byte* buffer, nuint length, out nuint read);
}
