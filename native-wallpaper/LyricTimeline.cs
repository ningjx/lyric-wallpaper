using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

internal readonly record struct LyricLine(double Time, string Text, string? Translation = null, bool IsMetadata = false);
internal readonly record struct LyricFrame(LyricLine[] Lines, double Focus, bool Moving, double Next, bool IsIdleClock = false);

internal sealed partial class LyricTimeline
{
    private WorkerSample? sample;
    private LyricLine[] song = [];
    private LyricLine[] idle = [];
    private DateTime clockMinute;
    private int target = -1;
    private double transitionAt, transitionFrom, pauseAt;
    private bool snap = true;

    public int Songs { get; private set; }
    public int LoadedLyrics { get; private set; }
    public int Pauses { get; private set; }
    public int Seeks { get; private set; }

    public void Update(WorkerSample? next, double now)
    {
        if (ReferenceEquals(sample, next)) return;
        var previous = sample;
        sample = next;
        if (next?.HasSong != true)
        {
            song = [];
            snap = true;
            return;
        }

        bool newSong = previous?.HasSong != true ||
            (previous.Track.Song, previous.Track.Author) != (next.Track.Song, next.Track.Author);
        if (newSong || !ReferenceEquals(next.Track, previous?.Track))
        {
            song = ParseTrack(next.Track);
            if (newSong) Songs++;
            if (song.Length > 1) LoadedLyrics++;
            snap = true;
        }
        if (!next.Playing && (newSong || previous?.Playing == true))
        {
            pauseAt = now;
            Pauses++;
            snap = true;
        }
        if (!newSong && previous is not null && Math.Abs(next.Position - PositionAt(previous)) > .75)
        {
            Seeks++;
            snap = true;
        }
    }

    public LyricFrame At(double now, double lead, WallpaperSettings settings)
    {
        int idleDelay = settings.IdleSeconds;
        if (sample?.HasSong != true || (!sample.Playing && now >= pauseAt + idleDelay))
        {
            snap = true;
            DateTime time = DateTime.Now;
            var minute = new DateTime(time.Year, time.Month, time.Day, time.Hour, time.Minute, 0);
            if (minute != clockMinute)
            {
                clockMinute = minute;
                idle = [new LyricLine(0, time.ToString("HH:mm"), time.ToString("M月d日 dddd", CultureInfo.GetCultureInfo("zh-CN")), true)];
            }
            return new LyricFrame(idle, 0, false, now + 60 - (time - minute).TotalSeconds, IsIdleClock: true);
        }

        double position = PositionAt(sample) + settings.OffsetMs / 1000d + (sample.Playing ? lead : 0);
        int line = FindLine(song, position);
        if (snap || !sample.Playing)
        {
            target = line;
            transitionFrom = line;
            snap = false;
        }
        else if (target != line)
        {
            transitionFrom = Focus(now, lead, settings.ScrollSpeed);
            transitionAt = now;
            target = line;
        }
        double focus = Focus(now, lead, settings.ScrollSpeed);
        double next = sample.Playing
            ? line + 1 < song.Length ? now + Math.Max(.001, song[line + 1].Time - position) : double.PositiveInfinity
            : pauseAt + idleDelay;
        return new LyricFrame(song, focus, focus != target, next);
    }

    private double Focus(double now, double lead, double speed)
    {
        double elapsed = now - transitionAt;
        if (elapsed >= lead || transitionFrom == target) return target;
        return target + (transitionFrom - target) * (1 + speed * elapsed) * Math.Exp(-speed * elapsed);
    }

    private static double PositionAt(WorkerSample current) =>
        Math.Max(0, current.Position + (current.Playing ? Stopwatch.GetElapsedTime(current.ReceivedAt).TotalSeconds : 0));

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

    private static LyricLine[] ParseTrack(WorkerTrack track)
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
