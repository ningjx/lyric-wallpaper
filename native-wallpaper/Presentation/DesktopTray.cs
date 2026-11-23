using System.Runtime.InteropServices;
using W = System.Windows;
using Ui = System.Windows.Controls;
using Fluent = Wpf.Ui.Controls;
using LyricWallpaper.Platform.Windows;
using LyricWallpaper.Playback;
using LyricWallpaper.Storage;
using LyricWallpaper.Wallpaper;

namespace LyricWallpaper.Presentation;

internal sealed class DesktopTray : IDisposable
{
    private readonly WallpaperStore wallpaperStore;
    private readonly PlaybackSettingsStore playbackStore;
    private readonly LyricsService worker;
    private readonly Func<WallpaperSettings> currentWallpaper;
    private readonly Action<WallpaperSettings> applyWallpaper;
    private readonly Func<bool> testEnabled;
    private readonly Action<bool> setTestMode;
    private readonly Icon? icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
    private readonly NotifyIcon tray;
    private readonly Ui.ContextMenu menu = new() { Placement = Ui.Primitives.PlacementMode.MousePoint };
    private readonly Ui.TextBlock status = new()
    {
        Text = "正在连接歌词采集", MaxWidth = 190, TextTrimming = W.TextTrimming.CharacterEllipsis
    };
    private readonly Ui.MenuItem statusItem;
    private readonly Ui.MenuItem testToggle;
    private readonly Ui.MenuItem startupToggle;
    private readonly Ui.MenuItem editWidgets;
    private readonly DesktopEditHotkey editHotkey;
    private readonly Func<bool> widgetEditorOpen;
    private SettingsWindow? settings;
    private PlaybackSnapshot? lastSample;
    private bool syncingStartup;

    public bool ExitRequested { get; private set; }

    public DesktopTray(WallpaperStore wallpaperStore, PlaybackSettingsStore playbackStore, LyricsService worker, Func<WallpaperSettings> currentWallpaper,
        Action<WallpaperSettings> applyWallpaper, Func<bool> testEnabled, Action<bool> setTestMode,
        Func<bool> widgetEditorOpen, Action toggleWidgetEditor)
    {
        this.wallpaperStore = wallpaperStore;
        this.playbackStore = playbackStore;
        this.worker = worker;
        this.currentWallpaper = currentWallpaper;
        this.applyWallpaper = applyWallpaper;
        this.testEnabled = testEnabled;
        this.setTestMode = setTestMode;
        this.widgetEditorOpen = widgetEditorOpen;
        SettingsTheme.Apply(menu);
        menu.FontFamily = SettingsTheme.FontFamily;
        menu.FontSize = SettingsTheme.FontSize;
        menu.Style = (W.Style)menu.FindResource("TrayMenuStyle");
        menu.Opened += (_, _) =>
        {
            // A menu opened from the shell tray must receive keyboard focus even with settings closed.
            if (W.PresentationSource.FromVisual(menu) is W.Interop.HwndSource source)
                SetForegroundWindow(source.Handle);
            menu.Focus();
        };
        tray = new NotifyIcon { Icon = icon ?? SystemIcons.Application, Text = "歌词壁纸" };
        AddAction("打开设置", Fluent.SymbolRegular.Settings20, () => Open());
        editWidgets = AddItem("编辑桌面控件", Fluent.SymbolRegular.Edit20);
        editWidgets.Click += (_, _) => ToggleWidgetEditor();
        editHotkey = new DesktopEditHotkey(ToggleWidgetEditor);
        if (editHotkey.Registered) editWidgets.InputGestureText = "Ctrl+Alt+E";
        void ToggleWidgetEditor()
        {
            menu.IsOpen = false;
            Run(toggleWidgetEditor);
            RefreshWidgetEditor();
        }
        statusItem = AddItem(status, Fluent.SymbolRegular.Play20);
        statusItem.IsEnabled = false;
        Ui.ToolTipService.SetShowOnDisabled(statusItem, true);
        testToggle = AddItem("测试滚动", Fluent.SymbolRegular.TextFont24);
        testToggle.IsCheckable = true;
        testToggle.IsChecked = testEnabled();
        testToggle.Checked += TestModeChanged;
        testToggle.Unchecked += TestModeChanged;
        AddSeparator();
        AddAction("歌词服务设置", Fluent.SymbolRegular.MusicNote224, () => Open(service: true));
        AddAction("重启歌词服务", Fluent.SymbolRegular.ArrowClockwise20, worker.Restart);
        startupToggle = AddItem("开机自启", Fluent.SymbolRegular.Power20);
        startupToggle.IsCheckable = true;
        startupToggle.Checked += StartupChanged;
        startupToggle.Unchecked += StartupChanged;
        AddAction("打开日志目录", Fluent.SymbolRegular.FolderOpen20, () => DesktopShell.OpenDirectory(playbackStore.LogDirectory));
        AddSeparator();
        AddAction("关于", Fluent.SymbolRegular.Info20, () => MessageBox.Show("歌词壁纸\nD3D11 桌面壁纸与本地歌词服务",
            "关于歌词壁纸", MessageBoxButtons.OK, MessageBoxIcon.Information));
        AddAction("退出歌词壁纸", Fluent.SymbolRegular.SignOut20, () => ExitRequested = true);
        tray.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            RefreshStartup();
            menu.IsOpen = true;
        };
        tray.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) Run(() => Open()); };
        tray.Visible = true;
    }

    private Ui.MenuItem AddItem(object header, Fluent.SymbolRegular symbol)
    {
        var item = new Ui.MenuItem
        {
            Header = header, Icon = new Fluent.SymbolIcon { Symbol = symbol, FontSize = 16 },
            Style = (W.Style)menu.FindResource("TrayMenuItemStyle")
        };
        menu.Items.Add(item);
        return item;
    }

    private void AddAction(string header, Fluent.SymbolRegular symbol, Action action) =>
        AddItem(header, symbol).Click += (_, _) => Run(action);

    private void AddSeparator() => menu.Items.Add(new Ui.Separator { Style = (W.Style)menu.FindResource("TrayMenuSeparatorStyle") });

    public void RefreshWidgetEditor() => editWidgets.Header = widgetEditorOpen() ? "结束控件编辑" : "编辑桌面控件";

    private void RefreshStartup()
    {
        bool? enabled = null;
        Run(() => enabled = DesktopShell.StartsAtLogin());
        syncingStartup = true;
        try
        {
            startupToggle.IsChecked = enabled == true;
            startupToggle.IsEnabled = enabled.HasValue;
            if (settings is { IsClosed: false }) settings.SetStartup(enabled);
        }
        finally { syncingStartup = false; }
    }

    private void StartupChanged(object sender, W.RoutedEventArgs e)
    {
        if (!syncingStartup) ChangeStartup(startupToggle.IsChecked);
    }

    private void ChangeStartup(bool enabled)
    {
        Run(() => DesktopShell.SetStartsAtLogin(enabled));
        RefreshStartup();
    }

    public void Open(bool service = false)
    {
        if (settings is null || settings.IsClosed)
        {
            settings = new SettingsWindow(wallpaperStore, playbackStore, currentWallpaper(), applyWallpaper, worker.Restart, ChangeTestMode, ChangeStartup);
            System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(settings);
        }
        settings.ShowPage(service);
        RefreshStartup();
        settings.SetTestMode(testEnabled());
        settings.SetStatus(status.Text);
        if (!settings.IsVisible) settings.Show();
        if (settings.WindowState == System.Windows.WindowState.Minimized)
            settings.WindowState = System.Windows.WindowState.Normal;
        settings.Activate();
    }

    public void Update(PlaybackSnapshot? sample)
    {
        lastSample = sample;
        string description = testEnabled() ? "测试滚动中"
            : sample is null ? "正在连接歌词采集"
            : !sample.HasSong ? "未检测到播放"
            : (sample.Playing ? "正在播放 · " : "已暂停 · ") + sample.Track.Song;
        if (status.Text == description) return;
        status.Text = description;
        statusItem.ToolTip = description;
        if (settings is { IsClosed: false }) settings.SetStatus(description);
    }

    private void ChangeTestMode(bool enabled)
    {
        if (testEnabled() == enabled) return;
        setTestMode(enabled);
        testToggle.IsChecked = enabled;
        if (settings is { IsClosed: false }) settings.SetTestMode(enabled);
        Update(lastSample);
    }

    private void TestModeChanged(object sender, W.RoutedEventArgs e)
    {
        Run(() => ChangeTestMode(testToggle.IsChecked));
        testToggle.IsChecked = testEnabled();
    }

    private void Run(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            tray.ShowBalloonTip(4500, "歌词壁纸", ex.Message, ToolTipIcon.Warning);
        }
    }

    public void Dispose()
    {
        tray.Visible = false;
        menu.IsOpen = false;
        if (settings is { IsClosed: false }) settings.CloseForExit();
        editHotkey.Dispose();
        tray.Dispose();
        icon?.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);
}
