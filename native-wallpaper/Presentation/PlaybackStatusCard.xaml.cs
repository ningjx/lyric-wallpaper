using LyricWallpaper.Playback;
using W = System.Windows;
using Ui = System.Windows.Controls;
using Media = System.Windows.Media;

namespace LyricWallpaper.Presentation;

internal partial class PlaybackStatusCard : Ui.UserControl, INativeGlassControl
{
    public PlaybackStatusCard() => InitializeComponent();

    public void Update(PlaybackSnapshot? sample)
    {
        StatusDot.Fill = (Media.Brush)FindResource(sample is { Playing: true }
            ? "SystemFillColorSuccessBrush" : "TextFillColorSecondaryBrush");
        if (sample is not { HasSong: true })
        {
            PlayState.Text = "";
            PlaybackDetails.Visibility = W.Visibility.Collapsed;
            Player.Text = sample switch
            {
                null => "正在连接歌词服务",
                { DetectionEnabled: false } => "播放器检测已关闭",
                _ => "等待播放"
            };
            ToolTip = sample is { DetectionEnabled: false } ? "可在“歌词服务”中开启播放器检测" : null;
            return;
        }

        ToolTip = null;
        Player.Text = sample.Source switch { "netease" => "网易云音乐", "applemusic" => "Apple Music", _ => "播放器" };
        PlayState.Text = sample.Playing ? "播放中" : "已暂停";
        PlaybackDetails.Visibility = W.Visibility.Visible;
        var track = sample.Track;
        Song.ToolTip = Song.Text = track.Song;
        Detail.ToolTip = Detail.Text = track.Author;
        Detail.Visibility = track.Author.Length == 0 ? W.Visibility.Collapsed : W.Visibility.Visible;
        LyricState.Text = sample.Lyrics switch
        {
            LyricActivity.Searching => "正在查找歌词",
            LyricActivity.Downloading => "正在下载歌词",
            LyricActivity.WaitingToRetry => "歌词等待重试",
            LyricActivity.Ready => "歌词已就绪",
            LyricActivity.Failed => "歌词获取失败",
            _ => "暂无可用歌词"
        };
        double position = sample.CurrentPosition;
        if (sample.Duration is double duration && double.IsFinite(duration) && duration > 0)
        {
            position = Math.Min(position, duration);
            Progress.Visibility = W.Visibility.Visible;
            Progress.Value = position / duration;
            ElapsedTime.Text = FormatTime(position);
            TotalTime.Text = FormatTime(duration);
        }
        else
        {
            Progress.Visibility = W.Visibility.Collapsed;
            ElapsedTime.Text = "已播放 " + FormatTime(position);
            TotalTime.Text = "";
        }
    }

    private static string FormatTime(double seconds)
    {
        long whole = (long)seconds;
        return whole < 3600 ? $"{whole / 60}:{whole % 60:00}" : $"{whole / 3600}:{whole / 60 % 60:00}:{whole % 60:00}";
    }

    public bool Compose(GlassFrame frame, W.Rect bounds, W.Rect clip, double elapsed)
    {
        const float radius = 17;
        var face = bounds;
        face.Inflate(-1, -1);
        frame.Shape(frame.Surfaces, face, clip, new(1, 1, 1, .24f), radius - 1, style: new(1, 1, .32f, 0));
        frame.Shape(frame.Lenses, bounds, clip, new(1, 1, 1, .12f), radius,
            new(8, -9, 2), new(1, 1, .84f, 0));
        return false;
    }
}
