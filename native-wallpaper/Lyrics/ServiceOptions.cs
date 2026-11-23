using System.Text.Json;

internal sealed record ServiceOptions
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    public MusicOptions Music { get; init; } = new();
    public NetworkOptions Netease { get; init; } = new();
    public CacheOptions Cache { get; init; } = new();
    public QueryOptions Lyrics { get; init; } = new();
    public HttpOptions Http { get; init; } = new();
    public string[] SourcePriority { get; init; } = ["applemusic", "netease"];

    public static ServiceOptions Load(string path, Action<string> log)
    {
        try
        {
            if (File.Exists(path))
            {
                var options = JsonSerializer.Deserialize<ServiceOptions>(File.ReadAllText(path), Json) ?? new();
                return options with
                {
                    Music = options.Music ?? new(), Netease = options.Netease ?? new(),
                    Cache = options.Cache ?? new(), Lyrics = options.Lyrics ?? new(),
                    Http = options.Http ?? new(), SourcePriority = options.SourcePriority ?? ["applemusic", "netease"]
                };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            log("读取歌词服务设置失败，使用默认值：" + ex.Message);
        }
        return new();
    }

    internal static double Seconds(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

internal sealed record MusicOptions
{
    public bool EnableNetease { get; init; } = true;
    public bool EnableApple { get; init; } = true;
    public double NeteasePollInterval { get; init; } = .2;
    public double ApplePollInterval { get; init; } = .2;
    public double TitleRecheckInterval { get; init; } = 30;
    public double SnapshotMaxAge { get; init; } = 2;
}

internal sealed record NetworkOptions
{
    public double Timeout { get; init; } = 5;
    public int Retries { get; init; } = 3;
    public int FuseThreshold { get; init; } = 3;
    public double FuseCooldown { get; init; } = 60;
    public double SearchCacheTtl { get; init; } = 300;
    public double SearchNegativeTtl { get; init; } = 15;
}

internal sealed record CacheOptions
{
    public string? DataDir { get; init; }
    public int LyricsMemCap { get; init; } = 128;
    public string LyricsDirectory => Path.Combine(string.IsNullOrWhiteSpace(DataDir)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LyricServer")
        : DataDir, "lyrics");
}

internal sealed record QueryOptions
{
    public string[] ProviderOrder { get; init; } = ["local-file", "netease", "qq"];
    public double TotalTimeout { get; init; } = 8;
    public bool Parallel { get; init; } = true;
}

internal sealed record HttpOptions
{
    public string? LogFile { get; init; }
}
