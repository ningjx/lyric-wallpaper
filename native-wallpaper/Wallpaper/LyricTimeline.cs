using System.Globalization;
using System.Text.RegularExpressions;
using LyricWallpaper.Playback;

namespace LyricWallpaper.Wallpaper;

internal readonly record struct LyricLine(double Time, string Text, string? Translation = null, bool IsMetadata = false);
internal readonly record struct LyricFrame(LyricLine[] Lines, double Focus, bool Moving, double Next, bool Idle = false);

internal sealed partial class LyricTimeline
{
    private PlaybackSnapshot? sample;
    private LyricLine[] song = [];
    private double pauseAt;

    public int Songs { get; private set; }
    public int LoadedLyrics { get; private set; }
    public int Pauses { get; private set; }
    public int Seeks { get; private set; }

    public void Update(PlaybackSnapshot? next, double now)
    {
        if (ReferenceEquals(sample, next)) return;
        var previous = sample;
        sample = next;
        if (next?.HasSong != true)
        {
            song = [];
            return;
        }

        bool newSong = previous?.HasSong != true ||
            (previous.Track.Song, previous.Track.Author) != (next.Track.Song, next.Track.Author);
        if (newSong || !ReferenceEquals(next.Track, previous?.Track))
        {
            song = ParseTrack(next.Track);
            if (newSong) Songs++;
            if (song.Length > 1) LoadedLyrics++;
        }
        if (!next.Playing && (newSong || previous?.Playing == true))
        {
            pauseAt = now;
            Pauses++;
        }
        if (!newSong && previous is not null && next.ReceivedAt != previous.ReceivedAt && Math.Abs(next.Position - previous.CurrentPosition) > .75)
            Seeks++;
    }

    public LyricFrame At(double now, double lead, WallpaperSettings settings)
    {
        int idleDelay = settings.IdleSeconds;
        if (sample?.HasSong != true || (!sample.Playing && now >= pauseAt + idleDelay))
            return new LyricFrame([], 0, false, double.PositiveInfinity, Idle: true);

        double position = sample.CurrentPosition + settings.OffsetMs / 1000d;
        return sample.Playing ? PlayingAt(song, position, now, lead, settings.ScrollSpeed)
            : new(song, FindLine(song, position), false, pauseAt + idleDelay);
    }

    internal static LyricFrame PlayingAt(LyricLine[] lines, double position, double now, double lead, double speed)
    {
        int line = FindLine(lines, position);
        if (line + 1 >= lines.Length) return new(lines, line, false, double.PositiveInfinity);

        // Arrive on the next cue, but never start before the current cue.
        double end = lines[line + 1].Time;
        double start = Math.Max(lines[line].Time, end - lead);
        if (position < start) return new(lines, line, false, now + (start - position));

        // LRC can assign several rows the same timestamp. Land on the same row FindLine will select.
        int target = line + 2 < lines.Length && lines[line + 2].Time == end ? FindLine(lines, end) : line + 1;
        double phase = (position - start) / (end - start);
        double progress = Step(phase * lead * speed) / Step(lead * speed);
        return new(lines, line + (target - line) * progress, true, now + (end - position));

        static double Step(double t) => 1 - (1 + t) * Math.Exp(-t);
    }

    private static int FindLine(LyricLine[] lines, double position)
    {
        int low = 0, high = lines.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (lines[middle].Time <= position) low = middle + 1;
            else high = middle;
        }
        return Math.Max(0, low - 1);
    }

    private static LyricLine[] ParseTrack(PlaybackTrack track)
    {
        var rows = new List<LyricLine> { new(double.NegativeInfinity, track.Song, track.Author, true) };
        LyricLine[] translations = Parse(track.Translation);
        foreach (var line in Parse(track.Lyrics))
        {
            string? translation = null;
            if (translations.Length > 0)
            {
                int index = FindLine(translations, line.Time + .01);
                if (index + 1 < translations.Length &&
                    Math.Abs(translations[index + 1].Time - line.Time) < Math.Abs(translations[index].Time - line.Time))
                    index++;
                if (Math.Abs(line.Time - translations[index].Time) < 8)
                    translation = translations[index].Text;
            }
            rows.Add(line with { Translation = translation });
        }
        return rows.ToArray();
    }

    private static LyricLine[] Parse(string lrc)
    {
        var lines = new List<LyricLine>();
        foreach (string raw in lrc.Split('\n'))
        {
            var text = raw.Trim();
            if (text.Length == 0 || text[0] == '{' || Metadata().IsMatch(text)) continue;
            var timestamps = Timestamp().Matches(text);
            text = Timestamp().Replace(text, "").Trim().TrimEnd("，。、；：！？．….,;:!?".ToCharArray());
            if (text.Length == 0) continue;
            foreach (Match match in timestamps)
            {
                double seconds = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 60 +
                    int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                if (match.Groups[3].Success)
                    seconds += int.Parse(match.Groups[3].Value.PadRight(3, '0'), CultureInfo.InvariantCulture) / 1000.0;
                lines.Add(new LyricLine(seconds, text));
            }
        }
        return lines.OrderBy(line => line.Time).ToArray();
    }

    [GeneratedRegex(@"\[([0-9]{1,2}):([0-9]{1,2})(?:[.:]([0-9]{1,3}))?\]")]
    private static partial Regex Timestamp();

    [GeneratedRegex(@"^\[[a-zA-Z]+:")]
    private static partial Regex Metadata();
}
