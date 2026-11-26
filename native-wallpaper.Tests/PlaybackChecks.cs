using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using LyricWallpaper.Playback;
using LyricWallpaper.Playback.Resolution;
using LyricWallpaper.Playback.Sources;

internal static class PlaybackChecks
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(3);

    public static async Task RunAsync(Action<bool, string> require)
    {
        var before = new byte[64];
        var after = new byte[64];
        Write(before, 8, 12, 1);
        Write(after, 8, 13.5, 1);
        Write(before, 32, 42, 1);
        Write(after, 32, 43.55, 1);
        var offsets = NeteaseSource.FindProgressOffset(before, after, 1.5, 1.5,
            candidate => candidate.Progress == 8 ? 13.5 : 45.02);
        require(offsets is { Progress: 32, Duration: 0, Rate: 40 },
            "NetEase probes moving progress across two intervals without guessing duration");

        var arbiter = new PlaybackArbiter(["applemusic", "netease"], TimeSpan.FromSeconds(2));
        var netease = new PlayerSnapshot("NetEase", "Artist", null, 25, true);
        var apple = new PlayerSnapshot("Apple", "Artist", 240, 15, true);
        arbiter.Update("netease", netease, 1);
        require(arbiter.Pick(1)?.Snapshot == netease, "NetEase can play with an unknown total duration");
        arbiter.Update("applemusic", apple, 2);
        arbiter.Update("netease", netease with { Duration = 180, Position = 26 }, 3);
        require(arbiter.Pick(3) is { Source: "applemusic", Snapshot: var picked } && picked == apple,
            "learning the duration leaves player arbitration stable");

        var folder = Directory.CreateTempSubdirectory("LyricWallpaper.Playback.Tests.");
        try
        {
            await CheckEarlyLyricsAsync(Path.Combine(folder.FullName, "early"), require);
            await CheckOnlinePreviewAsync(Path.Combine(folder.FullName, "online"), require);
            await CheckRetryAsync(Path.Combine(folder.FullName, "retry"), require);
            await CheckUnavailableAsync(Path.Combine(folder.FullName, "unavailable"), require);
            await CheckSwitchAsync(Path.Combine(folder.FullName, "switch"), require);
        }
        finally { folder.Delete(recursive: true); }

        static void Write(byte[] buffer, int at, double progress, double rate)
        {
            BitConverter.GetBytes(progress).CopyTo(buffer, at);
            BitConverter.GetBytes(rate).CopyTo(buffer, at + sizeof(double));
        }
    }

    private static async Task CheckOnlinePreviewAsync(string directory, Action<bool, string> require)
    {
        const string title = "Online Song", artist = "Artist";
        var options = Options(directory, ["netease", "qq"]);
        var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TaskCompletionSource<LyricsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        using var http = new LyricHttp(options.Netease, stop.Token, new TestResponses(async (request, cancellation) =>
        {
            if (request.RequestUri!.Host == "c.y.qq.com")
            {
                await releaseSlow.Task.WaitAsync(cancellation);
                return """{"code":0,"data":{"song":{"list":[]}}}""";
            }
            return request.RequestUri.AbsolutePath.Contains("search") ? Search(title, artist, 56) : Lyric("online");
        }));
        var resolver = new LyricResolver(options, http, _ => { });
        var pending = resolver.ResolveAsync(title, artist, stop.Token, result => first.TrySetResult(result));
        try
        {
            var early = await first.Task.WaitAsync(TestTimeout);
            require(!pending.IsCompleted && early is { Provider: "netease", Lrc: "[00:01.00]online" },
                "an exact online match appears while another provider is pending");
            releaseSlow.SetResult();
            await pending.WaitAsync(TestTimeout);
        }
        finally { await stop.CancelAsync(); }
    }

    private static async Task CheckEarlyLyricsAsync(string directory, Action<bool, string> require)
    {
        const string title = "Early Song", artist = "Artist", local = "[00:01.00]local";
        var options = Options(directory, ["local-file", "netease"]);
        Directory.CreateDirectory(options.Cache.LyricsDirectory);
        await File.WriteAllTextAsync(Path.Combine(options.Cache.LyricsDirectory, $"{title} - {artist}.lrc"), local);
        await File.WriteAllTextAsync(Path.Combine(options.Cache.LyricsDirectory, "123.lrc"), "[00:01.00]unrelated");
        var releaseNetwork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TaskCompletionSource<LyricsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        using var http = new LyricHttp(options.Netease, stop.Token, new TestResponses(async (request, cancellation) =>
        {
            await releaseNetwork.Task.WaitAsync(cancellation);
            return request.RequestUri!.AbsolutePath.Contains("search")
                ? Search(title, artist, 52)
                : Lyric("online", "translation");
        }));
        var resolver = new LyricResolver(options, http, _ => { });
        var pending = resolver.ResolveAsync(title, artist, stop.Token, result => first.TrySetResult(result));
        try
        {
            var early = await first.Task.WaitAsync(TestTimeout);
            require(!pending.IsCompleted && early is { Provider: "local-file", Lrc: local },
                "named local lyrics appear while network and ID lookup are pending");
            releaseNetwork.SetResult();
            var result = await pending.WaitAsync(TestTimeout);
            require(result is { Lrc: "[00:01.00]online", TranslatedLyric: "[00:01.00]translation" },
                "the completed lookup upgrades the preview with the best timed lyrics and translation");
        }
        finally { await stop.CancelAsync(); }
    }

    private static async Task CheckRetryAsync(string directory, Action<bool, string> require)
    {
        const string title = "Retry Song", artist = "Artist";
        var options = Options(directory, ["netease"]);
        int searches = 0;
        var heard = new TaskCompletionSource<PlaybackSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var changes = new ConcurrentQueue<PlaybackSnapshot>();
        using var stop = new CancellationTokenSource();
        using var http = new LyricHttp(options.Netease, stop.Token, new TestResponses((request, _) =>
        {
            var response = request.RequestUri!.AbsolutePath switch
            {
                "/api/search/get/web" => Interlocked.Increment(ref searches) == 1
                    ? """{"code":200,"result":{"songs":[]}}"""
                    : Search(title, artist, 53),
                "/eapi/search/get" => """{"code":200,"result":{"songs":[]}}""",
                _ => Lyric("recovered")
            };
            return Task.FromResult(response);
        }));
        var session = new PlaybackSession(options, new(options, http, _ => { }), snapshot =>
        {
            changes.Enqueue(snapshot);
            if (snapshot.Track.Lyrics.Length > 0) heard.TrySetResult(snapshot);
        }, _ => { }, stop.Token);
        var run = session.RunAsync(Path.Combine(directory, "config.json"));
        try
        {
            session.Update("netease", new(title, artist, null, 9, true));
            var snapshot = await heard.Task.WaitAsync(TestTimeout);
            require(snapshot is { Track.Song: title, Track.Lyrics: "[00:01.00]recovered" } && searches == 2,
                "lyrics recover after a miss while the same song stays selected");
            var songChanges = changes.Where(item => item.HasSong).ToArray();
            require(songChanges.Select(item => item.Lyrics).SequenceEqual([LyricActivity.Searching, LyricActivity.WaitingToRetry,
                LyricActivity.Searching, LyricActivity.Downloading, LyricActivity.Ready]) &&
                songChanges.All(item => item.ReceivedAt == snapshot.ReceivedAt && item.Duration is null),
                "lyric search, retry and download update the card without restarting the playback clock");
            session.Update("netease", new(title, artist, 180, 9, true));
            require(changes.Last() is { Source: "netease", Duration: 180 } latest && latest.ReceivedAt == snapshot.ReceivedAt,
                "learning the total duration updates progress even at the same position");
            session.Update("applemusic", new(title, artist, 180, 9, false));
            session.Update("netease", null);
            require(changes.Last() is { Source: "applemusic", Playing: false, CurrentPosition: 9, Lyrics: LyricActivity.Ready },
                "a paused player taking over the same song keeps its lyrics and exact elapsed time");
            session.Update("applemusic", null);
            require(changes.Last() is { HasSong: false, Duration: null, Lyrics: LyricActivity.Idle, Track.Song: "" },
                "losing the player clears the card's song, time and lyrics");
        }
        finally
        {
            await stop.CancelAsync();
            await run.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static async Task CheckUnavailableAsync(string directory, Action<bool, string> require)
    {
        var options = Options(directory, ["local-file"]);
        using var stop = new CancellationTokenSource();
        using var http = new LyricHttp(options.Netease, stop.Token);
        var finished = new TaskCompletionSource<PlaybackSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new PlaybackSession(options, new(options, http, _ => { }), snapshot =>
        {
            if (snapshot.Lyrics == LyricActivity.Unavailable) finished.TrySetResult(snapshot);
        }, _ => { }, stop.Token);
        var run = session.RunAsync(Path.Combine(directory, "config.json"));
        try
        {
            session.Update("applemusic", new("Missing lyrics", "", 120, 3, false));
            var final = await finished.Task.WaitAsync(TestTimeout);
            require(final is { Track.Song: "Missing lyrics", Playing: false, Duration: 120, CurrentPosition: 3 },
                "exhausting lyric retries publishes the result while playback stays paused");
        }
        finally
        {
            await stop.CancelAsync();
            await run.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static async Task CheckSwitchAsync(string directory, Action<bool, string> require)
    {
        const string oldTitle = "Old Song", newTitle = "New Song", artist = "Artist";
        var options = Options(directory, ["netease"]);
        var oldRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returnedOld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heardNew = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var displayed = new ConcurrentQueue<PlaybackTrack>();
        using var stop = new CancellationTokenSource();
        using var http = new LyricHttp(options.Netease, stop.Token, new TestResponses(async (request, cancellation) =>
        {
            var url = Uri.UnescapeDataString(request.RequestUri!.AbsoluteUri);
            if (url.Contains(oldTitle, StringComparison.Ordinal))
            {
                oldRequested.SetResult();
                await releaseOld.Task.WaitAsync(cancellation);
                returnedOld.SetResult();
                return Search(oldTitle, artist, 54);
            }
            if (url.Contains(newTitle, StringComparison.Ordinal)) return Search(newTitle, artist, 55);
            return url.Contains("id=55", StringComparison.Ordinal) ? Lyric("new") : Lyric("old");
        }));
        var session = new PlaybackSession(options, new(options, http, _ => { }), snapshot =>
        {
            if (snapshot.Track.Lyrics.Length == 0) return;
            displayed.Enqueue(snapshot.Track);
            if (snapshot.Track.Song == newTitle) heardNew.TrySetResult();
        }, _ => { }, stop.Token);
        var run = session.RunAsync(Path.Combine(directory, "config.json"));
        try
        {
            session.Update("netease", new(oldTitle, artist, null, 20, true));
            await oldRequested.Task.WaitAsync(TestTimeout);
            session.Update("netease", new(newTitle, artist, null, 0, true));
            await heardNew.Task.WaitAsync(TestTimeout);
            releaseOld.SetResult();
            await returnedOld.Task.WaitAsync(TestTimeout);
        }
        finally
        {
            await stop.CancelAsync();
            await run.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        require(displayed.All(item => item.Song == newTitle && item.Lyrics == "[00:01.00]new"),
            "a superseded lyric lookup cannot overwrite the current song");
    }

    private static ServiceOptions Options(string directory, string[] providers) => new()
    {
        Netease = new() { Retries = 0, SearchNegativeTtl = .1 },
        Cache = new() { DataDir = directory },
        Lyrics = new() { ProviderOrder = providers },
        Music = new() { EnableApple = false, EnableNetease = false }
    };

    private static string Search(string name, string artist, int id) =>
        JsonSerializer.Serialize(new { code = 200, result = new { songs = new[] { new { name, id, artists = new[] { new { name = artist } } } } } });

    private static string Lyric(string text, string? translation = null) =>
        JsonSerializer.Serialize(new { code = 200, lrc = new { lyric = "[00:01.00]" + text },
            tlyric = new { lyric = translation is null ? "" : "[00:01.00]" + translation } });

    private sealed class TestResponses(Func<HttpRequestMessage, CancellationToken, Task<string>> send) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            new(HttpStatusCode.OK) { Content = new StringContent(await send(request, cancellationToken)) };
    }
}
