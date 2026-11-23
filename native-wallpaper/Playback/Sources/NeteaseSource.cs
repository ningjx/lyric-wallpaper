using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using LyricWallpaper.Playback;
using LyricWallpaper.Playback.Resolution;

namespace LyricWallpaper.Playback.Sources;

internal sealed class NeteaseSource(MusicOptions options, string configPath, Action<string> log)
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(3);
    private const string ProcessName = "cloudmusic", ModuleName = "cloudmusic.dll", OffsetFile = "offsets_config.json";
    private readonly string offsetPath = Path.Combine(Path.GetDirectoryName(configPath)!, OffsetFile);

    public async Task RunAsync(Action<PlayerSnapshot?> publish, CancellationToken stop)
    {
        var interval = TimeSpan.FromSeconds(ServiceOptions.Seconds(options.NeteasePollInterval, .05, 60, .2));
        double retry = 10;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var target = FindPlayer();
                if (target is not null)
                {
                    var offsets = await ResolveOffsetsAsync(target, stop);
                    if (offsets is { } known)
                    {
                        retry = 10;
                        await PollAsync(target, known, interval, publish, stop);
                        publish(null);
                        await Task.Delay(interval, stop);
                        continue;
                    }
                    retry = Math.Min(retry * 1.5, 60);
                }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                log("网易云采集将重试：" + ex.Message);
            }
            publish(null);
            await Task.Delay(TimeSpan.FromSeconds(retry), stop);
        }
    }

    private async Task PollAsync(PlayerMemory target, PlaybackOffsets offsets, TimeSpan interval,
        Action<PlayerSnapshot?> publish, CancellationToken stop)
    {
        string song = "", artist = "";
        double previousDuration = -1, positionSample = 0;
        long titleCheckedAt = 0, positionCheckedAt = 0;
        bool playing = true;
        int stationary = 0;
        while (!stop.IsCancellationRequested && target.TryReadPlayback(offsets, out double position, out double duration, out _))
        {
            long now = Stopwatch.GetTimestamp();
            var roundedDuration = Math.Round(duration, 1);
            double titleRecheck = ServiceOptions.Seconds(options.TitleRecheckInterval, .05, 3600, 30);
            if (song.Length == 0) titleRecheck = Math.Min(2, titleRecheck);
            if (roundedDuration != previousDuration || Stopwatch.GetElapsedTime(titleCheckedAt, now).TotalSeconds >= titleRecheck)
            {
                (song, artist) = ReadTitle(target.Id);
                previousDuration = roundedDuration;
                titleCheckedAt = now;
            }
            double elapsed = Stopwatch.GetElapsedTime(positionCheckedAt, now).TotalSeconds;
            if (positionCheckedAt == 0 || elapsed >= .35)
            {
                if (positionCheckedAt != 0)
                {
                    if (position - positionSample > elapsed * .15) { playing = true; stationary = 0; }
                    else if (++stationary >= 2) playing = false;
                }
                positionSample = position;
                positionCheckedAt = now;
            }
            publish(song.Length == 0 || duration <= 0 ? null : new PlayerSnapshot(song, artist, duration, position, playing));
            await Task.Delay(interval, stop);
        }
    }

    private async Task<PlaybackOffsets?> ResolveOffsetsAsync(PlayerMemory target, CancellationToken stop)
    {
        var store = LoadOffsets();
        if (store.TryGetValue(target.Version, out var cached) &&
            target.TryReadPlayback(cached, out _, out _, out _, validating: true)) return cached;

        log($"正在探测网易云 {target.Version} 的播放进度和歌曲时长。");
        var before = target.ReadImage(stop);
        await Task.Delay(ProbeInterval, stop);
        if (target.HasExited) return null;
        var after = target.ReadImage(stop);
        var discovered = FindOffsets(before, after);
        if (discovered is not { } offsets || !target.TryReadPlayback(offsets, out _, out _, out _, validating: true)) return null;
        store[target.Version] = offsets;
        try { LyricCache.WriteJson(offsetPath, store); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log("无法保存网易云偏移缓存：" + ex.Message); }
        return offsets;
    }

    private Dictionary<string, PlaybackOffsets> LoadOffsets()
    {
        var values = new Dictionary<string, PlaybackOffsets> { ["3.1.28.8527"] = new(0x1D808F8, 0x1DE1038, 0x1D80900) };
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
                    if (entry.Progress > 0 && entry.Duration > 0 && entry.Rate > 0) values[version] = entry;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
    }

    internal static PlaybackOffsets? FindOffsets(byte[] before, byte[] after)
    {
        var previous = MemoryMarshal.Cast<byte, double>(before);
        var current = MemoryMarshal.Cast<byte, double>(after);
        int length = Math.Min(previous.Length, current.Length);
        const int durationDistance = 0x60000 / sizeof(double), tolerance = 0x20000 / sizeof(double);
        foreach (bool strict in new[] { true, false })
        {
            bool foundProgress = false;
            for (int i = 0; i + 1 < length; i++)
            {
                double progress = previous[i], delta = current[i] - progress, rate = current[i + 1];
                if (!(progress is >= 0 and <= 3600) || !(strict
                    ? delta is >= 2.5 and <= 3.5 && rate is >= .99 and <= 1.01
                    : delta is >= 1.5 and <= 5.5 && Math.Abs(rate) >= .5)) continue;
                foundProgress = true;
                int center = i + durationDistance, best = -1, bestDistance = int.MaxValue;
                for (int j = center - tolerance; j < Math.Min(center + tolerance, length); j++)
                {
                    double duration = previous[j];
                    if (!(duration is >= 10 and <= 86400) || duration <= current[i] || !(Math.Abs(duration - current[j]) < .001)) continue;
                    int distance = Math.Abs(j - center);
                    if (distance >= bestDistance) continue;
                    (best, bestDistance) = (j, distance);
                }
                if (best >= 0) return new(i * 8, best * 8, (i + 1) * 8);
            }
            if (foundProgress) break; // Match the original strict-first probe.
        }
        return null;
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

        public bool TryReadPlayback(PlaybackOffsets offsets, out double position, out double duration, out double rate, bool validating = false)
        {
            position = duration = rate = 0;
            Span<byte> playback = stackalloc byte[16];
            Span<byte> value = stackalloc byte[8];
            if (!Read(offsets.Progress, playback) || !Read(offsets.Duration, value)) return false;
            position = BitConverter.ToDouble(playback);
            duration = BitConverter.ToDouble(value);
            if (offsets.Rate == offsets.Progress + 8) rate = BitConverter.ToDouble(playback[8..]);
            else if (Read(offsets.Rate, value)) rate = BitConverter.ToDouble(value);
            else return false;
            if (!double.IsFinite(position) || !double.IsFinite(duration)) return false;
            return !validating || position is >= 0 and <= 86400 && duration is >= 10 and <= 86400 &&
                (Math.Abs(rate) < .01 || Math.Abs(rate) is >= .5 and <= 2);
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
