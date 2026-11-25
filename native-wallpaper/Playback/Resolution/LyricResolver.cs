using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LyricWallpaper.Playback;

namespace LyricWallpaper.Playback.Resolution;

internal sealed class LyricResolver(ServiceOptions options, LyricHttp http, Action<string> log)
{
    private const string Netease = "netease";
    private const string QQ = "qq";
    private const string Local = "local-file";
    private const string NeteaseReferer = "https://music.163.com/";
    private const string QQReferer = "https://y.qq.com/";
    private const string EapiHost = "https://interface3.music.163.com";
    private readonly string directory = options.Cache.LyricsDirectory;
    private readonly LyricCache cache = new(options.Cache.LyricsDirectory, options.Cache.LyricsMemCap);
    private readonly ConcurrentDictionary<(string Vendor, SongKey Song), (Match? Match, long Until)> searches = new();
    private readonly ConcurrentDictionary<SongKey, long> misses = new();
    private readonly ConcurrentDictionary<string, long> cooldowns = new();

    internal TimeSpan MissRetryDelay => TimeSpan.FromSeconds(
        ServiceOptions.Seconds(options.Netease.SearchNegativeTtl, .05, 86400, 15));

    public async Task<LyricsResult?> ResolveAsync(string title, string artist, CancellationToken stop,
        Action<LyricsResult>? onAvailable = null)
    {
        SongKey song = SongKey.Normalize(title, artist);
        if (cache.Get(song) is { } previous) return previous;
        if (misses.TryGetValue(song, out long until) && until > Environment.TickCount64) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(ServiceOptions.Seconds(options.Lyrics.TotalTimeout, .05, 300, 8)));
        var cancellation = deadline.Token;
        var netease = new Lazy<Task<Match?>>(() => SearchAsync(Netease, title, artist, song, cancellation));
        string[] order = options.Lyrics.ProviderOrder ?? [Local, Netease, QQ];
        var eligible = order.Where(vendor => vendor is Local or Netease or QQ).Distinct().ToArray();
        if (eligible.Length == 0) eligible = [Local];
        LyricsResult? result = null;
        var previewGate = new object();
        LyricsResult? preview = null;
        int previewRank = int.MaxValue;
        if (options.Lyrics.Parallel)
        {
            var results = await Task.WhenAll(eligible.Select(QueryWithPreviewAsync));
            result = results.OfType<LyricsResult>()
                .OrderByDescending(item => item.Similarity).ThenByDescending(item => item.Completeness()).FirstOrDefault();
        }
        else
        {
            foreach (string vendor in eligible)
            {
                if (cancellation.IsCancellationRequested) break;
                result = await QueryAsync(vendor, onAvailable);
                if (result is not null) break;
            }
        }
        stop.ThrowIfCancellationRequested();
        if (result is not null)
        {
            cache.Put(song, result);
            misses.TryRemove(song, out _);
        }
        else misses[song] = Environment.TickCount64 + (long)MissRetryDelay.TotalMilliseconds;
        return result;

        async Task<LyricsResult?> QueryWithPreviewAsync(string vendor, int rank)
        {
            var candidate = await QueryAsync(vendor, local => OfferPreview(local, rank));
            OfferPreview(candidate, rank);
            return candidate;
        }

        void OfferPreview(LyricsResult? candidate, int rank)
        {
            if (onAvailable is null || candidate is not { Similarity: 100 }) return;
            lock (previewGate)
            {
                if (stop.IsCancellationRequested) return;
                if (preview is not null)
                {
                    int completeness = candidate.Completeness().CompareTo(preview.Completeness());
                    if (completeness < 0 || (completeness == 0 && rank > previewRank)) return;
                    if (candidate == preview && rank == previewRank) return;
                }
                preview = candidate;
                previewRank = rank;
                onAvailable(candidate);
            }
        }

        async Task<LyricsResult?> QueryAsync(string vendor, Action<LyricsResult>? onNamedFile)
        {
            if (cooldowns.TryGetValue(vendor, out long blocked) && blocked > Environment.TickCount64) return null;
            try
            {
                return vendor switch
                {
                    Local => await ReadLocalAsync(title, artist, netease, cancellation, onNamedFile),
                    Netease => await ReadNeteaseAsync(await netease.Value, artist, cancellation),
                    _ => await ReadQQAsync(await SearchAsync(QQ, title, artist, song, cancellation), artist, cancellation)
                };
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return null; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                cooldowns[vendor] = Expiry(15, 15);
                log($"歌词源 {vendor} 查询失败：{ex.Message}");
                return null;
            }
        }
    }

    private async Task<LyricsResult?> ReadLocalAsync(string title, string artist, Lazy<Task<Match?>> search,
        CancellationToken stop, Action<LyricsResult>? onNamedFile)
    {
        if (!Directory.Exists(directory)) return null;
        string[] named = [$"{title.Trim()} - {artist.Trim()}.lrc", $"{title.Trim()}.lrc"];
        string? namedPath = named.Select(LocalPath).FirstOrDefault(file => file is not null);
        var namedResult = namedPath is null ? null : await ReadAsync(namedPath);
        if (namedResult is not null && !stop.IsCancellationRequested) onNamedFile?.Invoke(namedResult);
        // A matching ID file takes precedence within the local provider.
        if (Directory.EnumerateFiles(directory, "*.lrc").Any(file => Path.GetFileNameWithoutExtension(file).All(char.IsAsciiDigit)))
        {
            try
            {
                if (await search.Value is { } match && match.Id.All(char.IsAsciiDigit))
                {
                    string? idPath = LocalPath(match.Id + ".lrc");
                    if (idPath is not null) return await ReadAsync(idPath) ?? namedResult;
                }
            }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        return namedResult;

        async Task<LyricsResult?> ReadAsync(string path)
        {
            try
            {
                string lrc = await File.ReadAllTextAsync(path, stop);
                return lrc.Length == 0 ? null : new(Local, true, lrc, Meta: new() { ["path"] = path }, Similarity: 100);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log($"无法读取本地歌词 {path}：{ex.Message}");
                return null;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return null; }
        }

        string? LocalPath(string name)
        {
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            string candidate = Path.Combine(directory, name);
            return File.Exists(candidate) ? candidate : null;
        }
    }

    private async Task<Match?> SearchAsync(string vendor, string title, string artist, SongKey song, CancellationToken stop)
    {
        var key = (vendor, song);
        if (searches.TryGetValue(key, out var entry) && entry.Until > Environment.TickCount64) return entry.Match;
        string query = (title + " " + artist).Trim();
        Match? match;
        if (vendor == Netease)
        {
            var response = await GetAsync(NeteaseReferer + "api/search/get/web",
                new() { ["s"] = query, ["type"] = "1", ["limit"] = "5" }, NeteaseReferer, "search:netease-public:" + query, stop);
            match = PickNetease(response);
            if (match is null)
            {
                response = await PostEapiAsync("/api/search/get",
                    new() { ["s"] = query, ["limit"] = "5", ["offset"] = "0", ["type"] = "1", ["csrf_token"] = "" },
                    "search:netease:" + query, stop);
                match = PickNetease(response);
            }
        }
        else
        {
            var response = await GetAsync("https://c.y.qq.com/soso/fcgi-bin/client_search_cp", new()
            {
                ["ct"] = "24", ["qqmusic_ver"] = "1298", ["remoteplace"] = "txt.yqq.center", ["t"] = "0",
                ["aggr"] = "1", ["cr"] = "1", ["catZhida"] = "1", ["lossless"] = "0", ["flag_qc"] = "0",
                ["p"] = "1", ["n"] = "8", ["w"] = query, ["g_tk"] = "5381", ["loginUin"] = "0",
                ["hostUin"] = "0", ["format"] = "json", ["inCharset"] = "utf8", ["outCharset"] = "utf-8",
                ["notice"] = "0", ["platform"] = "yqq", ["needNewCode"] = "0"
            }, QQReferer, "search:qq:" + query, stop);
            match = Field(response, "code") == "0"
                ? PickBest(At(At(At(response, "data"), "song"), "list"), "songname", "singer", "songid", title, artist) : null;
        }
        searches[key] = (match, match is null ? Expiry(options.Netease.SearchNegativeTtl, 15) : Expiry(options.Netease.SearchCacheTtl, 300));
        return match;

        Match? PickNetease(JsonElement response) => Field(response, "code") == "200"
            ? PickBest(At(At(response, "result"), "songs"), "name", "artists", "id", title, artist) : null;
    }

    private static Match? PickBest(JsonElement candidates, string titleKey, string artistsKey, string idKey,
        string title, string artist)
    {
        if (candidates.ValueKind != JsonValueKind.Array) return null;
        Match? best = null;
        foreach (var candidate in candidates.EnumerateArray())
        {
            string id = Field(candidate, idKey);
            if (id.Length == 0) continue;
            var artists = At(candidate, artistsKey);
            string author = artists.ValueKind == JsonValueKind.Array
                ? string.Join(" / ", artists.EnumerateArray().Select(item => Field(item, "name"))).Trim() : "";
            int similar = SongSimilarity.Calculate(title, artist, Field(candidate, titleKey).Trim(), author);
            if (best is null || similar > best.Similarity) best = new(id, similar);
            if (similar == 100) break;
        }
        return best;
    }

    private async Task<LyricsResult?> ReadNeteaseAsync(Match? match, string artist, CancellationToken stop)
    {
        if (match is null || match.Similarity < SongSimilarity.Threshold(artist)) return null;
        string id = match.Id;
        var response = await GetAsync(NeteaseReferer + "api/song/lyric",
            new() { ["id"] = id, ["lv"] = "-1", ["kv"] = "-1", ["tv"] = "-1" },
            NeteaseReferer, "lyric:netease-public:" + id, stop);
        var publicLyric = ParseNetease(response, "klyric");
        if (publicLyric is not null) return publicLyric;
        response = await PostEapiAsync("/api/song/lyric/v1", new()
        {
            ["id"] = id, ["cp"] = "false", ["lv"] = "0", ["kv"] = "0", ["tv"] = "0", ["rv"] = "0",
            ["yv"] = "0", ["ytv"] = "0", ["yrv"] = "0", ["csrf_token"] = ""
        }, "lyric:netease:" + id, stop);
        return ParseNetease(response, "yrc");

        LyricsResult? ParseNetease(JsonElement json, string karaoke)
        {
            string lrc = Field(At(json, "lrc"), "lyric");
            return Field(json, "code") != "200" || lrc.Length == 0 ? null
                : new(Netease, true, lrc, Field(At(json, "tlyric"), "lyric"), Field(At(json, karaoke), "lyric"),
                    new() { ["netease_id"] = id }, match.Similarity);
        }
    }

    private async Task<LyricsResult?> ReadQQAsync(Match? match, string artist, CancellationToken stop)
    {
        if (match is null || match.Similarity < SongSimilarity.Threshold(artist)) return null;
        var response = await GetAsync("https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg", new()
        {
            ["musicid"] = match.Id, ["g_tk"] = "5381", ["loginUin"] = "0", ["hostUin"] = "0", ["format"] = "json",
            ["inCharset"] = "utf8", ["outCharset"] = "utf8", ["notice"] = "0", ["platform"] = "yqq",
            ["needNewCode"] = "0", ["nobase64"] = "1"
        }, QQReferer, "lyric:qq:" + match.Id, stop);
        string lrc = Field(response, "lyric");
        return lrc.Length == 0 || Field(response, "code") == "-1901" ? null
            : new(QQ, true, lrc, Field(response, "trans"), Meta: new() { ["qq_id"] = match.Id }, Similarity: match.Similarity);
    }

    private async Task<JsonElement> GetAsync(string url, Dictionary<string, string> parameters, string referer,
        string key, CancellationToken stop) =>
        Parse(await http.RequestAsync(url, parameters, referer, key, stop, expectJson: referer == NeteaseReferer));

    private async Task<JsonElement> PostEapiAsync(string path, Dictionary<string, string> payload, string key, CancellationToken stop) =>
        Parse(await http.RequestAsync(EapiHost + "/e" + path, new() { ["params"] = EncryptEapi(path, payload) },
            NeteaseReferer, key, stop, post: true, expectJson: true));

    // Adapted from Widdit/now-playing-service's EapiHelper (WXRIW, Apache-2.0).
    private static string EncryptEapi(string path, Dictionary<string, string> payload)
    {
        long milliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        payload["header"] = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["__csrf"] = "", ["appver"] = "8.0.0", ["buildver"] = (milliseconds / 1000).ToString(),
            ["channel"] = "", ["deviceId"] = "", ["mobilename"] = "", ["resolution"] = "1920x1080",
            ["os"] = "android", ["osver"] = "", ["requestId"] = $"{milliseconds}_{milliseconds % 10000:0000}",
            ["versioncode"] = "140", ["MUSIC_U"] = ""
        });
        var jsonOptions = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        string json = JsonSerializer.Serialize(payload, jsonOptions);
        string digest = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes("nobody" + path + "use" + json + "md5forencrypt")));
        using var aes = Aes.Create();
        aes.Key = "e82ckenh8dichen8"u8.ToArray();
        return Convert.ToHexString(aes.EncryptEcb(Encoding.UTF8.GetBytes($"{path}-36cd479b6b5-{json}-36cd479b6b5-{digest}"),
            PaddingMode.PKCS7));
    }

    private static JsonElement Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return default;
        text = text.Trim();
        if (text[0] != '{')
        {
            int start = text.IndexOf('(');
            int end = text.LastIndexOf(')');
            if (start < 0 || end <= start) return default;
            text = text[(start + 1)..end];
        }
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException) { return default; }
    }

    private static JsonElement At(JsonElement json, string name) =>
        json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var value) ? value : default;

    private static string Field(JsonElement json, string name)
    {
        var value = At(json, name);
        return value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : "";
    }

    private static long Expiry(double seconds, double fallback) =>
        Environment.TickCount64 + (long)(1000 * ServiceOptions.Seconds(seconds, 0, 86400, fallback));

    private sealed record Match(string Id, int Similarity);
}
