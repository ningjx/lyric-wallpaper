using System.Text.RegularExpressions;

// Ported from server/lyrics/similarity.py (SongMatchingUtil attribution applies).

namespace LyricWallpaper.Playback.Resolution;

internal static class SongSimilarity
{
    private const string Traditional = "國語體樂後來時間個們無為與愛會對邊這說聽萬點實麼還讓開關學習動沒問幾樣種總寶東貝見門風飛馬聲覺觀進過選員車轉龍魚鳥";
    private const string Simplified = "国语体乐后来时间个们无为与爱会对边这说听万点实么还让开关学习动没问几样种总宝东贝见门风飞马声觉观进过选员车转龙鱼鸟";
    private static readonly string[] Significant =
        ["remix", "live", "acoustic", "instrumental", "cover", "edit", "mix", "dj", "radio", "extended", "remaster", "remastered",
         "unplugged", "demo", "bootleg", "mashup", "orchestral", "symphony", "stripped", "sped up", "slowed", "reverb", "0.8x", "1.1x",
         "1.2x", "现场", "翻唱", "伴奏", "钢琴版", "吉他版", "改编", "翻自", "重制", "混音", "慢速", "快速", "加速", "倍速", "粤语版", "填词", "dj版"];
    private static readonly string[] Harmless =
        ["explicit", "clean", "original mix", "feat", "ft.", "翻译", "译名", "又名", "别名", "原名", "aka"];
    private static readonly Regex VersionPattern = new(@"\b(ver\.?|version|edition|remix|live|acoustic|instrumental|cover|demo|remaster(ed)?|unplugged|karaoke|extended|radio)\b");
    private static readonly Regex ExtrasPattern = new(@"\(([^)]+)\)");
    private static readonly Regex BaseTitlePattern = new(@"\s*\([^)]*\)\s*");

    public static int Threshold(string artist) => string.IsNullOrWhiteSpace(artist) ? 75 : 85;

    private static string Normalize(string text)
    {
        var chars = text.ToLowerInvariant().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char value = chars[i];
            int scriptIndex = Traditional.IndexOf(value);
            chars[i] = scriptIndex >= 0 ? Simplified[scriptIndex] : value switch
            {
                '[' or '（' or '【' or '「' or '『' or '〔' or '〈' => '(',
                ']' or '）' or '】' or '」' or '』' or '〕' or '〉' => ')',
                >= '！' and <= '～' => (char)(value - 0xFEE0),
                _ => value
            };
        }
        return Regex.Replace(new string(chars), @"\s+", " ").Trim();
    }

    private static int Match(string left, string right)
    {
        left = left.Trim();
        right = right.Trim();
        if (left.Length == 0 || right.Length == 0) return 0;
        if (left == right) return 100;
        if (left.Contains(right, StringComparison.Ordinal))
            return 70 + (right.Length * 100 / left.Length) * 30 / 100;
        if (right.Contains(left, StringComparison.Ordinal))
            return 70 + (left.Length * 100 / right.Length) * 30 / 100;
        int[] row = Enumerable.Range(0, right.Length + 1).ToArray();
        for (int i = 1; i <= left.Length; i++)
        {
            int diagonal = row[0];
            row[0] = i;
            for (int j = 1; j <= right.Length; j++)
            {
                int previous = row[j];
                row[j] = Math.Min(Math.Min(previous, row[j - 1]) + 1, diagonal + (left[i - 1] == right[j - 1] ? 0 : 1));
                diagonal = previous;
            }
        }
        return Math.Max(0, 100 - row[^1] * 100 / Math.Max(left.Length, right.Length));
    }

    private static string BaseTitle(string value) => BaseTitlePattern.Replace(value, " ").Trim();
    private static string[] Extras(string value) => ExtrasPattern.Matches(value).Select(match => match.Groups[1].Value.Trim())
        .Where(extra => extra.Length > 0).ToArray();
    private static bool IsSignificant(string extra) => Significant.Any(extra.Contains) || VersionPattern.IsMatch(extra);
    private static bool IsHarmless(string extra) => Harmless.Any(extra.Contains);

    private static HashSet<string> Keywords(string[] extras)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (string extra in extras.Where(IsSignificant))
        {
            var words = Significant.Where(extra.Contains).ToArray();
            result.UnionWith(words.Length == 0 ? ["?"] : words);
        }
        return result;
    }

    private static int ExtraPenalty(string[] local, string[] cloud)
    {
        if (local.Length == 0 && cloud.Length == 0) return 0;
        if (cloud.Length == 0) return local.Any(IsSignificant) ? 55 : local.All(IsHarmless) ? 5 : 25;
        if (local.Length == 0) return cloud.Any(IsSignificant) ? 50 : cloud.All(IsHarmless) ? 2 * cloud.Length : 15;
        var left = Keywords(local);
        var right = Keywords(cloud);
        if (left.Count > 0 && right.Count == 0) return 50;
        if (right.Count > 0 && left.Count == 0) return 45;
        if (left.Count > 0)
        {
            int intersection = left.Count(right.Contains);
            if (intersection == 0) return 55;
            if (intersection != left.Count || intersection != right.Count)
                return (int)((1 - intersection / (double)Math.Max(left.Count, right.Count)) * 40);
        }
        return (local.Sum(extra => cloud.Max(item => Match(extra, item))) / local.Length) switch
        {
            >= 90 => 5, >= 70 => 15, >= 50 => 30, _ => 45
        };
    }

    private static int ArtistScore(string local, string cloud)
    {
        string[] left = local.Split('/').Select(BaseTitle).Where(name => name.Length > 0).ToArray();
        string[] right = cloud.Split('/').Select(BaseTitle).Where(name => name.Length > 0).ToArray();
        if (left.Length == 0 || right.Length == 0) return left.Length == right.Length ? 100 : 50;
        int matched = left.Count(name => right.Any(candidate => Match(name, candidate) >= 80));
        if (matched == 0) return 0;
        if (left.Length == 1) return 100;
        int reverse = right.Count(name => left.Any(candidate => Match(name, candidate) >= 80));
        return Math.Max(55, (int)(matched / (double)left.Length * 70 + reverse / (double)right.Length * 30));
    }

    public static int Calculate(string title, string artist, string candidateTitle, string candidateArtist)
    {
        if (title.Length == 0 || candidateTitle.Length == 0) return 0;
        (title, candidateTitle) = (Normalize(title), Normalize(candidateTitle));
        (artist, candidateArtist) = (Normalize(artist), Normalize(candidateArtist));
        int match = Match(BaseTitle(title), BaseTitle(candidateTitle));
        int titleScore = title == candidateTitle ? 100 : match switch
        {
            < 70 => match / 4,
            < 85 => match / 3,
            < 95 => match / 2,
            _ => Math.Max(0, match - ExtraPenalty(Extras(title), Extras(candidateTitle)))
        };
        if (titleScore < 30) return titleScore;
        int artistScore = ArtistScore(artist, candidateArtist);
        if (artistScore == 0 && artist.Length > 0 && candidateArtist.Length > 0) return Math.Min(titleScore / 2, 40);
        return (int)(titleScore * .65 + artistScore * .35);
    }
}
