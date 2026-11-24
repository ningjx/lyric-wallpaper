using System.Globalization;
using System.Text.Json.Nodes;
using W = System.Windows;
using Ui = System.Windows.Controls;
using Input = System.Windows.Input;
using Media = System.Windows.Media;
using Motion = System.Windows.Media.Animation;
using Fluent = Wpf.Ui.Controls;
using LyricWallpaper.Platform.Windows;
using LyricWallpaper.Scene;
using LyricWallpaper.Storage;
using LyricWallpaper.Wallpaper;

namespace LyricWallpaper.Presentation;

internal sealed class SettingsWindow : Fluent.FluentWindow
{
    private const double ControlHeight = 32;
    private static readonly (string Label, Fluent.SymbolRegular Icon)[] Categories =
    [
        ("背景", Fluent.SymbolRegular.Image24),
        ("歌词排版", Fluent.SymbolRegular.TextFont24),
        ("空间层次", Fluent.SymbolRegular.Stack24),
        ("玻璃", Fluent.SymbolRegular.Drop24),
        ("高光与阴影", Fluent.SymbolRegular.WeatherSunny24),
        ("歌词服务", Fluent.SymbolRegular.MusicNote224)
    ];
    private readonly Media.Brush ink, muted, line, surface, error;
    private readonly WallpaperStore wallpaperStore;
    private readonly PlaybackSettingsStore playbackStore;
    private readonly Action<WallpaperSettings> applyWallpaper;
    private readonly Action restartWorker;
    private readonly Dictionary<int, W.FrameworkElement> pages = new();
    private readonly Ui.ListBox nav = new();
    private readonly Ui.Grid page = new();
    private readonly Ui.TextBlock title = new();
    private readonly Ui.TextBlock status = new();
    private readonly Ui.TextBlock errorNotice = new() { Visibility = W.Visibility.Collapsed };
    private readonly Fluent.ToggleSwitch testToggle = new()
    {
        Content = "测试滚动",
        VerticalContentAlignment = W.VerticalAlignment.Center
    };
    private readonly Fluent.ToggleSwitch startupToggle = new()
    {
        Content = "开机自启", IsEnabled = false, MinHeight = ControlHeight,
        VerticalContentAlignment = W.VerticalAlignment.Center
    };
    private readonly W.Threading.DispatcherTimer wallpaperUpdate = new() { Interval = TimeSpan.FromMilliseconds(25) };
    private readonly W.Threading.DispatcherTimer wallpaperSave = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly W.Threading.DispatcherTimer serviceUpdate = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private WallpaperSettings wallpaper;
    private WallpaperSettings? queuedWallpaper;
    private Func<bool>? saveService;
    private bool wallpaperDirty, serviceDirty, exiting, syncingStartup;
    private W.FrameworkElement? visiblePage;

    public bool IsClosed { get; private set; }

    public SettingsWindow(WallpaperStore wallpaperStore, PlaybackSettingsStore playbackStore, WallpaperSettings wallpaper,
        Action<WallpaperSettings> applyWallpaper, Action restartWorker, Action<bool> setTestMode, Action<bool> setStartup)
    {
        this.wallpaperStore = wallpaperStore;
        this.playbackStore = playbackStore;
        this.wallpaper = wallpaper;
        this.applyWallpaper = applyWallpaper;
        this.restartWorker = restartWorker;
        SettingsTheme.Apply(this);
        ink = ThemeBrush("TextFillColorPrimaryBrush");
        muted = ThemeBrush("TextFillColorSecondaryBrush");
        line = ThemeBrush("CardStrokeColorDefaultBrush");
        surface = ThemeBrush("CardBackgroundFillColorDefaultBrush");
        error = ThemeBrush("SystemFillColorCriticalBrush");
        FontFamily = SettingsTheme.FontFamily;
        FontSize = SettingsTheme.FontSize;
        Title = "歌词壁纸 · 设置";
        Width = 780;
        Height = 600;
        MinWidth = 670;
        MinHeight = 490;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = Fluent.WindowBackdropType.Mica;
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        Foreground = ink;
        WindowStartupLocation = W.WindowStartupLocation.CenterScreen;
        wallpaperUpdate.Tick += (_, _) => ApplyQueuedWallpaper();
        wallpaperSave.Tick += (_, _) => SaveWallpaper();
        serviceUpdate.Tick += (_, _) => ApplyService();
        testToggle.ToolTip = "让演示歌词在桌面上循环滚动，便于调节壁纸效果。";
        W.Automation.AutomationProperties.SetName(testToggle, "测试滚动");
        testToggle.Checked += (_, _) => setTestMode(true);
        testToggle.Unchecked += (_, _) => setTestMode(false);
        W.Automation.AutomationProperties.SetName(startupToggle, "开机自启");
        void StartupChanged(object sender, W.RoutedEventArgs e)
        {
            if (!syncingStartup) setStartup(startupToggle.IsChecked == true);
        }
        startupToggle.Checked += StartupChanged;
        startupToggle.Unchecked += StartupChanged;
        Closing += (_, _) =>
        {
            ApplyQueuedWallpaper();
            SaveWallpaper();
            ApplyService();
        };
        Closed += (_, _) => IsClosed = true;
        Content = MakeLayout();
        nav.SelectedIndex = 0;
    }

    public void SetStatus(string text) => status.Text = text;
    public void SetTestMode(bool enabled) => testToggle.IsChecked = enabled;

    public void SetStartup(bool? enabled)
    {
        syncingStartup = true;
        try
        {
            startupToggle.IsEnabled = enabled.HasValue;
            startupToggle.IsChecked = enabled == true;
        }
        finally { syncingStartup = false; }
    }

    public void CloseForExit()
    {
        exiting = true;
        Close();
    }

    public void ShowPage(bool service)
    {
        if (service) nav.SelectedIndex = Categories.Length - 1;
        else if (nav.SelectedIndex < 0 || nav.SelectedIndex == Categories.Length - 1) nav.SelectedIndex = 0;
    }

    private W.FrameworkElement MakeLayout()
    {
        var root = new Ui.DockPanel();
        status.Foreground = muted;
        status.VerticalAlignment = W.VerticalAlignment.Center;
        status.Margin = new W.Thickness(12, 0, 0, 0);
        var actions = new Ui.StackPanel { Orientation = Ui.Orientation.Horizontal,
            Margin = new W.Thickness(0, 0, 14, 0), VerticalAlignment = W.VerticalAlignment.Center };
        status.MaxWidth = 210;
        status.TextTrimming = W.TextTrimming.CharacterEllipsis;
        actions.Children.Add(testToggle);
        actions.Children.Add(status);
        var header = new Fluent.TitleBar { Title = Title, TrailingContent = actions, Height = 38 };
        Ui.DockPanel.SetDock(header, Ui.Dock.Top);
        root.Children.Add(header);

        errorNotice.Foreground = error;
        errorNotice.TextWrapping = W.TextWrapping.Wrap;
        errorNotice.Margin = new W.Thickness(19, 2, 19, 9);
        Ui.DockPanel.SetDock(errorNotice, Ui.Dock.Bottom);
        root.Children.Add(errorNotice);

        var body = new Ui.Grid { Margin = new W.Thickness(15, 0, 15, 12) };
        body.ColumnDefinitions.Add(new Ui.ColumnDefinition { Width = new W.GridLength(132) });
        body.ColumnDefinitions.Add(new Ui.ColumnDefinition());
        foreach (var (label, icon) in Categories)
        {
            var row = new Ui.StackPanel { Orientation = Ui.Orientation.Horizontal };
            row.Children.Add(new Fluent.SymbolIcon { Symbol = icon, FontSize = 16, Margin = new W.Thickness(0, 0, 9, 0) });
            row.Children.Add(new Ui.TextBlock { Text = label, VerticalAlignment = W.VerticalAlignment.Center });
            var item = SettingsListItem(row);
            W.Automation.AutomationProperties.SetName(item, label);
            nav.Items.Add(item);
        }
        W.Automation.AutomationProperties.SetName(nav, "设置分类");
        nav.Background = Media.Brushes.Transparent;
        nav.Padding = new W.Thickness(4);
        nav.BorderThickness = new W.Thickness(0);
        nav.Margin = new W.Thickness(0, 0, 10, 0);
        nav.SelectionChanged += (_, _) => SwitchCategory(nav.IsMouseOver && !nav.IsStylusOver &&
            Input.Mouse.LeftButton == Input.MouseButtonState.Pressed);
        var sidebar = new Ui.DockPanel();
        startupToggle.Margin = new W.Thickness(7, 0, 0, 2);
        Ui.DockPanel.SetDock(startupToggle, Ui.Dock.Bottom);
        sidebar.Children.Add(startupToggle);
        sidebar.Children.Add(nav);
        body.Children.Add(sidebar);

        var section = new Ui.DockPanel { Margin = new W.Thickness(16, 10, 12, 3) };
        title.FontSize = 17;
        title.Foreground = ink;
        title.FontWeight = W.FontWeights.SemiBold;
        title.Margin = new W.Thickness(0, 0, 0, 8);
        Ui.DockPanel.SetDock(title, Ui.Dock.Top);
        section.Children.Add(title);
        section.Children.Add(new Ui.ScrollViewer
        {
            Content = page, VerticalScrollBarVisibility = Ui.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Ui.ScrollBarVisibility.Disabled
        });
        var card = new Ui.Border { BorderBrush = line, BorderThickness = new W.Thickness(1),
            Background = surface, CornerRadius = new W.CornerRadius(8), Child = section };
        Ui.Grid.SetColumn(card, 1);
        body.Children.Add(card);
        root.Children.Add(body);
        return root;
    }

    private void SwitchCategory(bool pointerSelection)
    {
        int index = nav.SelectedIndex;
        if (index < 0) return;
        if (!pages.TryGetValue(index, out var view))
        {
            view = index switch
            {
                0 => MakeBackground(),
                1 => MakeLyrics(),
                2 => MakeDepth(),
                3 => MakeGlass(),
                4 => MakeLight(),
                _ => MakeService()
            };
            pages.Add(index, view);
            page.Children.Add(view);
        }
        bool changingPage = visiblePage is not null && visiblePage != view;
        // Keep inactive controls in the visual tree so their theme resources stay available.
        if (changingPage && visiblePage is { } previous)
        {
            previous.BeginAnimation(OpacityProperty, null);
            previous.Visibility = W.Visibility.Collapsed;
        }
        view.Visibility = W.Visibility.Visible;
        if (changingPage && pointerSelection && W.SystemParameters.ClientAreaAnimation)
            AnimateOpacity(view, .84, 1, 130);
        visiblePage = view;
        title.Text = Categories[index].Label;
        errorNotice.Visibility = W.Visibility.Collapsed;
    }

    private Ui.ListBoxItem SettingsListItem(W.FrameworkElement content)
    {
        var item = new Ui.ListBoxItem { Content = content, Style = (W.Style)FindResource("SettingsListItemStyle") };
        void Hover(Input.MouseEventArgs e, double target)
        {
            if (item.Template?.FindName("HoverBackground", item) is not Ui.Border background) return;
            double previous = background.Opacity;
            background.Opacity = target;
            if (e.StylusDevice is null && W.SystemParameters.ClientAreaAnimation)
                AnimateOpacity(background, previous, target, 105);
            else
                background.BeginAnimation(OpacityProperty, null);
        }
        item.MouseEnter += (_, e) => Hover(e, 1);
        item.MouseLeave += (_, e) => Hover(e, 0);
        return item;
    }

    private static void AnimateOpacity(W.UIElement element, double from, double to, int milliseconds)
    {
        var animation = new Motion.DoubleAnimationUsingKeyFrames { FillBehavior = Motion.FillBehavior.Stop };
        animation.KeyFrames.Add(new Motion.DiscreteDoubleKeyFrame(from, Motion.KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new Motion.SplineDoubleKeyFrame(to, Motion.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(milliseconds)),
            new Motion.KeySpline(.23, 1, .32, 1)));
        element.BeginAnimation(OpacityProperty, animation);
    }

    private void QueueWallpaper(Func<WallpaperSettings, WallpaperSettings> edit)
    {
        queuedWallpaper = edit(queuedWallpaper ?? wallpaper);
        if (!wallpaperUpdate.IsEnabled) wallpaperUpdate.Start();
    }

    private void ApplyQueuedWallpaper()
    {
        wallpaperUpdate.Stop();
        var updated = queuedWallpaper?.Normalize();
        queuedWallpaper = null;
        if (updated is null || updated == wallpaper) return;
        TryAction(() =>
        {
            applyWallpaper(updated);
            wallpaper = updated;
            wallpaperDirty = true;
            wallpaperSave.Stop();
            wallpaperSave.Start();
        });
    }

    private void SaveWallpaper()
    {
        wallpaperSave.Stop();
        if (!wallpaperDirty) return;
        TryAction(() =>
        {
            wallpaperStore.Save(wallpaper);
            wallpaperDirty = false;
        });
    }

    private void QueueService()
    {
        serviceDirty = true;
        serviceUpdate.Stop();
        serviceUpdate.Start();
    }

    private void ApplyService()
    {
        serviceUpdate.Stop();
        if (!serviceDirty || saveService is null) return;
        TryAction(() =>
        {
            bool changed = saveService();
            serviceDirty = false;
            if (changed && !exiting) restartWorker();
        });
    }

    private W.FrameworkElement MakeBackground()
    {
        var grid = Fields();
        var source = PathLabel("", "纯色背景");
        void ShowSource(string image, string directory)
        {
            bool rotating = directory.Length != 0;
            string path = rotating ? directory : image;
            source.Text = path.Length == 0 ? "纯色背景" : (rotating ? "轮播 · " : "单图 · ") + path;
            source.ToolTip = path.Length == 0 ? null : path;
        }
        void SelectSource(string image, string directory)
        {
            QueueWallpaper(s => s with { Background = image, BackgroundDirectory = directory });
            ShowSource(image, directory);
        }
        ShowSource(wallpaper.Background, wallpaper.BackgroundDirectory);

        var menu = new Ui.ContextMenu { FontFamily = FontFamily, FontSize = FontSize };
        SettingsTheme.Apply(menu);
        menu.Style = (W.Style)menu.FindResource("TrayMenuStyle");
        var itemStyle = (W.Style)menu.FindResource("TrayMenuItemStyle");
        void AddSourceAction(string label, Fluent.SymbolRegular icon, Action action)
        {
            var item = new Ui.MenuItem
            {
                Header = label, Style = itemStyle, Icon = new Fluent.SymbolIcon { Symbol = icon, FontSize = 16 }
            };
            item.Click += (_, _) => TryAction(action);
            menu.Items.Add(item);
        }
        var select = new Fluent.DropDownButton { Content = "选择", Flyout = menu, MinHeight = ControlHeight };
        W.Automation.AutomationProperties.SetName(select, "选择背景来源");
        menu.PlacementTarget = select;
        menu.Placement = Ui.Primitives.PlacementMode.Bottom;
        select.Click += (_, _) => menu.IsOpen = true;

        AddSourceAction("选择图片…", Fluent.SymbolRegular.Image24, () =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择壁纸背景",
                Filter = "图片|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff"
            };
            if (picker.ShowDialog(this) != true) return;
            using var checkedImage = BackgroundImages.Load(wallpaper, picker.FileName);
            SelectSource(picker.FileName, "");
        });
        AddSourceAction("选择文件夹…", Fluent.SymbolRegular.FolderOpen20, () =>
        {
            if (PickFolder("选择轮播文件夹") is { } directory) SelectSource("", directory);
        });
        AddSourceAction("纯色背景", Fluent.SymbolRegular.PaintBucket20, () => SelectSource("", ""));
        AddField(grid, "背景来源", Inline(source, select), full: true);
        Option(grid, "布局",
            [("裁切填充", BackgroundLayout.Cover), ("完整显示", BackgroundLayout.Contain),
             ("拉伸", BackgroundLayout.Stretch), ("平铺", BackgroundLayout.Tile)],
            wallpaper.Layout, (s, v) => s with { Layout = v }, full: true);
        Integer(grid, "轮播间隔（秒）", wallpaper.RotationSeconds, 10, 3600, (s, v) => s with { RotationSeconds = v }, 10);
        Integer(grid, "背景缩放（%）", wallpaper.BackgroundZoom, 10, 400, (s, v) => s with { BackgroundZoom = v }, 5);
        Integer(grid, "背景水平位置（%）", wallpaper.BackgroundX, -100, 100, (s, v) => s with { BackgroundX = v });
        Integer(grid, "背景垂直位置（%）", wallpaper.BackgroundY, -100, 100, (s, v) => s with { BackgroundY = v });
        return grid;
    }

    private W.FrameworkElement MakeLyrics()
    {
        var grid = Fields();
        string family = wallpaper.FontFamily;
        var font = PathLabel(family, "");
        AddField(grid, "字体", Inline(font, Button("选择…", () =>
        {
            using var current = new System.Drawing.Font(family, 12);
            using var picker = new System.Windows.Forms.FontDialog { Font = current, FontMustExist = true };
            if (picker.ShowDialog(new DialogOwner(new W.Interop.WindowInteropHelper(this).Handle)) != System.Windows.Forms.DialogResult.OK) return;
            family = picker.Font.Name;
            SetPath(font, family, "");
            QueueWallpaper(s => s with { FontFamily = family });
        })));
        ColorField(grid, "文字颜色", wallpaper.TextColor, wallpaper.Opacity / 100d,
            (s, color, opacity) => s with { TextColor = color, Opacity = (int)Math.Round(opacity * 100) },
            minimum: .2, wholePercent: true);
        Boolean(grid, "自适应文字对比度", wallpaper.AdaptiveContrast, (s, v) => s with { AdaptiveContrast = v },
            "背景与文字过于接近时，自动加浅色或深色细描边，保留所选文字颜色。");
        Integer(grid, "字号（%）", wallpaper.FontScale, 50, 160, (s, v) => s with { FontScale = v });
        Integer(grid, "行距（%）", wallpaper.Spacing, 35, 175, (s, v) => s with { Spacing = v });
        Integer(grid, "背板内边距", wallpaper.Padding, 0, 160, (s, v) => s with { Padding = v });
        Option(grid, "屏幕对齐",
            [("靠左", LyricAlignment.Left), ("居中", LyricAlignment.Center), ("靠右", LyricAlignment.Right)],
            wallpaper.Alignment, (s, v) => s with { Alignment = v });
        Integer(grid, "歌词垂直微调", wallpaper.TextY, -40, 40, (s, v) => s with { TextY = v });
        Integer(grid, "整体水平（%）", wallpaper.Horizontal, -52, 52, (s, v) => s with { Horizontal = v });
        Integer(grid, "整体垂直（%）", wallpaper.Vertical, -56, 56, (s, v) => s with { Vertical = v });
        Decimal(grid, "滚动速度", wallpaper.ScrollSpeed, 2, 20, .1, (s, v) => s with { ScrollSpeed = v });
        Integer(grid, "歌词提前（毫秒）", wallpaper.OffsetMs, -5000, 5000, (s, v) => s with { OffsetMs = v }, 50);
        Integer(grid, "暂停后显示时钟（秒）", wallpaper.IdleSeconds, 0, 3600, (s, v) => s with { IdleSeconds = v }, 5);
        Integer(grid, "滚动帧率上限", wallpaper.Fps, 15, 144, (s, v) => s with { Fps = v });
        Decimal(grid, "渲染像素比（DPR）", wallpaper.RenderScale, .5, 2, .05, (s, v) => s with { RenderScale = v });
        Boolean(grid, "显示歌词译文", wallpaper.Translation, (s, v) => s with { Translation = v });
        return grid;
    }

    private W.FrameworkElement MakeDepth()
    {
        var grid = Fields();
        Decimal(grid, "远处最小缩放", wallpaper.FarScale, .35, .95, .01, (s, v) => s with { FarScale = v });
        Decimal(grid, "缩放衰减", wallpaper.ScaleFalloff, .05, 2, .01, (s, v) => s with { ScaleFalloff = v });
        Decimal(grid, "缩放曲线", wallpaper.ScaleCurve, .5, 4, .01, (s, v) => s with { ScaleCurve = v });
        Decimal(grid, "淡出衰减", wallpaper.FadeFalloff, .05, 2, .01, (s, v) => s with { FadeFalloff = v });
        Decimal(grid, "淡出曲线", wallpaper.FadeCurve, .5, 4, .01, (s, v) => s with { FadeCurve = v });
        Decimal(grid, "远处玻璃保留", wallpaper.FarGlass, 0, 1, .01, (s, v) => s with { FarGlass = v });
        Integer(grid, "渲染距离（行）", wallpaper.RenderDistance, 1, 6, (s, v) => s with { RenderDistance = v });
        Boolean(grid, "启用景深层", wallpaper.DepthEffect, (s, v) => s with { DepthEffect = v });
        return grid;
    }

    private W.FrameworkElement MakeGlass()
    {
        var grid = Fields();
        Integer(grid, "玻璃强度（%）", wallpaper.Glass, 0, 125, (s, v) => s with { Glass = v });
        Integer(grid, "折射高度", wallpaper.RefractionHeight, 0, 56, (s, v) => s with { RefractionHeight = v });
        Integer(grid, "折射强度", wallpaper.RefractionAmount, -80, 20, (s, v) => s with { RefractionAmount = v });
        Boolean(grid, "镜片立体折射", wallpaper.OpticalDepth, (s, v) => s with { OpticalDepth = v },
            "折射方向同时参考轮廓和卡片中心，不影响歌词远近的缩放与淡化。");
        Integer(grid, "背景模糊", wallpaper.Blur, 0, 36, (s, v) => s with { Blur = v });
        Boolean(grid, "高质量全场模糊", wallpaper.FullSceneBlur, (s, v) => s with { FullSceneBlur = v },
            "使用可分离高斯模糊。普通模糊和高质量模糊都不会影响玻璃外的背景。");
        Boolean(grid, "渐进模糊", wallpaper.ProgressiveBlur, (s, v) => s with { ProgressiveBlur = v },
            "背景模糊大于 0 时，中心柔化、边缘保留清晰折射；不会模糊背面歌词。");
        Integer(grid, "圆角", wallpaper.CornerRadius, 0, 180, (s, v) => s with { CornerRadius = v });
        Boolean(grid, "连续曲率圆角", wallpaper.ContinuousCorners, (s, v) => s with { ContinuousCorners = v },
            "让直边平滑过渡到圆角；关闭后使用普通圆弧。");
        Decimal(grid, "饱和度", wallpaper.Saturation, 0, 2.5, .01, (s, v) => s with { Saturation = v });
        Decimal(grid, "亮度", wallpaper.Brightness, -.5, .5, .01, (s, v) => s with { Brightness = v });
        Decimal(grid, "对比度", wallpaper.Contrast, .3, 2, .01, (s, v) => s with { Contrast = v });
        ColorField(grid, "染色", wallpaper.TintColor, wallpaper.TintAlpha,
            (s, color, opacity) => s with { TintColor = color, TintAlpha = opacity }, maximum: .6);
        Boolean(grid, "色相染色", wallpaper.HueTint, (s, v) => s with { HueTint = v },
            "先替换背景色相，再薄叠染色，保留更多背景明暗细节。");
        ColorField(grid, "表面色", wallpaper.SurfaceColor, wallpaper.SurfaceAlpha,
            (s, color, opacity) => s with { SurfaceColor = color, SurfaceAlpha = opacity }, maximum: .6);
        Option(grid, "色散", [("关闭", ChromaticMode.Off), ("RGB 色差", ChromaticMode.Rgb), ("七路光谱", ChromaticMode.Spectral)],
            wallpaper.Dispersion, (s, v) => s with { Chromatic = v == ChromaticMode.Rgb, SpectralDispersion = v == ChromaticMode.Spectral });
        Boolean(grid, "歌词置于玻璃背面", wallpaper.BehindGlass, (s, v) => s with { BehindGlass = v });
        return grid;
    }

    private W.FrameworkElement MakeLight()
    {
        var grid = Fields();
        Boolean(grid, "启用高光", wallpaper.Highlight, (s, v) => s with { Highlight = v });
        var followWallpaper = Boolean(grid, "控件高光跟随壁纸", wallpaper.WidgetWallpaperHighlight,
            (s, v) => s with { WidgetWallpaperHighlight = v },
            "从壁纸可见区域识别最多四个明显亮区，各控件按光源方向、颜色和距离混合高光。没有明显亮区时沿用手动值。");
        Option(grid, "高光模式",
            [("默认", HighlightStyle.Directional), ("环境", HighlightStyle.Ambient), ("纯净", HighlightStyle.Even)],
            wallpaper.HighlightMode, (s, v) => s with { HighlightMode = v });
        ColorField(grid, "高光颜色", wallpaper.HighlightColor, wallpaper.HighlightAlpha,
            (s, color, opacity) => s with { HighlightColor = color, HighlightAlpha = opacity },
            canEditColor: () => followWallpaper.IsChecked != true);
        var highlightAngle = Decimal(grid, "高光角度", wallpaper.HighlightAngle, -3.14, 3.14, .01, (s, v) => s with { HighlightAngle = v });
        void UpdateManualHighlight()
        {
            bool enabled = followWallpaper.IsChecked != true;
            highlightAngle.IsEnabled = enabled;
            highlightAngle.Opacity = enabled ? 1 : .5;
        }
        followWallpaper.Checked += (_, _) => UpdateManualHighlight();
        followWallpaper.Unchecked += (_, _) => UpdateManualHighlight();
        UpdateManualHighlight();
        Decimal(grid, "高光衰减", wallpaper.HighlightFalloff, .2, 5, .01, (s, v) => s with { HighlightFalloff = v });
        Decimal(grid, "高光边缘宽度", wallpaper.HighlightWidth, .2, 6, .1, (s, v) => s with { HighlightWidth = v });
        Boolean(grid, "启用外阴影", wallpaper.Shadow, (s, v) => s with { Shadow = v });
        ColorField(grid, "阴影颜色", wallpaper.ShadowColor, wallpaper.ShadowAlpha,
            (s, color, opacity) => s with { ShadowColor = color, ShadowAlpha = opacity });
        Integer(grid, "阴影模糊半径", wallpaper.ShadowRadius, 0, 80, (s, v) => s with { ShadowRadius = v });
        Integer(grid, "阴影水平偏移", wallpaper.ShadowX, -50, 50, (s, v) => s with { ShadowX = v });
        Integer(grid, "阴影垂直偏移", wallpaper.ShadowY, -50, 80, (s, v) => s with { ShadowY = v });
        Boolean(grid, "启用内阴影", wallpaper.InnerShadow, (s, v) => s with { InnerShadow = v });
        Decimal(grid, "内阴影不透明度", wallpaper.InnerShadowAlpha, 0, 1, .01, (s, v) => s with { InnerShadowAlpha = v });
        Integer(grid, "内阴影模糊半径", wallpaper.InnerShadowRadius, 0, 32, (s, v) => s with { InnerShadowRadius = v });
        Integer(grid, "内阴影水平偏移", wallpaper.InnerShadowX, -24, 24, (s, v) => s with { InnerShadowX = v });
        Integer(grid, "内阴影垂直偏移", wallpaper.InnerShadowY, -24, 24, (s, v) => s with { InnerShadowY = v });
        return grid;
    }

    private W.FrameworkElement MakeService()
    {
        var grid = Fields();
        JsonObject loaded;
        try { loaded = playbackStore.Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            AddField(grid, "无法读取配置", Text(ex.Message, 13, muted), full: true);
            AddField(grid, "", Button("打开日志目录", () => DesktopShell.OpenDirectory(playbackStore.LogDirectory)));
            return grid;
        }
        var music = loaded["music"] as JsonObject;
        var lyrics = loaded["lyrics"] as JsonObject;
        var cache = loaded["cache"] as JsonObject;
        var network = loaded["netease"] as JsonObject;
        var netease = Check("网易云音乐", PlaybackSettingsStore.Read(music, "enable_netease", true));
        var apple = Check("Apple Music", PlaybackSettingsStore.Read(music, "enable_apple", true));
        const string priorityLabel = "播放器优先级";
        var priority = Choice(priorityLabel, ["Apple Music 优先", "网易云音乐优先"],
            (loaded["source_priority"] as JsonArray)?.FirstOrDefault()?.ToString() == "netease" ? 1 : 0);
        var caching = new Fluent.TextBox { Text = PlaybackSettingsStore.Read(cache, "data_dir", ""), MinHeight = ControlHeight };
        caching.ToolTip = "留空时使用默认缓存目录";
        W.Automation.AutomationProperties.SetName(caching, "歌词缓存目录");
        caching.TextChanged += (_, _) => QueueService();
        var parallel = Check("并行查询歌词源", PlaybackSettingsStore.Read(lyrics, "parallel", true));
        foreach (var check in new[] { netease, apple, parallel })
        {
            check.Checked += (_, _) => QueueService();
            check.Unchecked += (_, _) => QueueService();
        }
        priority.SelectionChanged += (_, _) => QueueService();
        AddField(grid, "播放器", Inline(netease, apple));
        AddField(grid, priorityLabel, priority);
        var readNetease = ServiceNumber(grid, "网易云采样（秒）", music, "netease_poll_interval", .2, .05, 5, .05);
        var readApple = ServiceNumber(grid, "Apple Music 采样（秒）", music, "apple_poll_interval", .2, .05, 5, .05);
        var expiry = ServiceNumber(grid, "采集失联判定（秒）", music, "snapshot_max_age", 2, .2, 60, .1);
        var timeout = ServiceNumber(grid, "歌词总超时（秒）", lyrics, "total_timeout", 8, 1, 60, .5);
        var request = ServiceNumber(grid, "单次网络超时（秒）", network, "timeout", 5, 1, 60, .5);
        var retries = ServiceNumber(grid, "网络重试次数", network, "retries", 3, 0, 10, 1);

        var list = new Ui.ListBox { MinHeight = 95, MaxHeight = 150, Padding = new W.Thickness(4) };
        list.SetResourceReference(Ui.ListBox.BackgroundProperty, "ControlFillColorDefaultBrush");
        list.BorderBrush = line;
        list.BorderThickness = new W.Thickness(1);
        var known = new Dictionary<string, string> { ["local-file"] = "本地歌词", ["netease"] = "网易云音乐", ["qq"] = "QQ 音乐" };
        var enabled = lyrics?["provider_order"] is JsonArray order
            ? order.Select(item => item?.ToString() ?? "").Where(key => key.Length != 0).Distinct().ToArray()
            : known.Keys.ToArray();
        var providerKeys = enabled.Concat(known.Keys.Except(enabled)).ToArray();
        var providerChecks = providerKeys.Select(key => Check(known.GetValueOrDefault(key, key), enabled.Contains(key))).ToArray();
        foreach (var check in providerChecks)
        {
            check.Checked += (_, _) => QueueService();
            check.Unchecked += (_, _) => QueueService();
            list.Items.Add(SettingsListItem(check));
        }
        void Move(int delta)
        {
            int index = list.SelectedIndex, destination = index + delta;
            if (index < 0 || destination < 0 || destination >= providerKeys.Length) return;
            // Move the data while keeping both WPF UI check boxes in the theme's resource scope.
            (providerKeys[index], providerKeys[destination]) = (providerKeys[destination], providerKeys[index]);
            var source = providerChecks[index];
            var target = providerChecks[destination];
            (source.Content, target.Content) = (target.Content, source.Content);
            (source.IsChecked, target.IsChecked) = (target.IsChecked, source.IsChecked);
            list.SelectedIndex = destination;
            list.ScrollIntoView(list.Items[destination]);
            QueueService();
        }
        AddField(grid, "歌词源及顺序", Inline(list, Button("上移", () => Move(-1)), Button("下移", () => Move(1))), full: true);
        AddField(grid, "歌词缓存目录", Inline(caching, Button("选择目录…", () =>
        {
            if (PickFolder("选择歌词缓存目录") is { } directory) caching.Text = directory;
        }), Button("默认", () => caching.Text = "")), full: true);
        AddField(grid, "", parallel);

        saveService = () =>
        {
            var keys = providerKeys.Where((_, index) => providerChecks[index].IsChecked == true).ToArray();
            if (keys.Length == 0) throw new InvalidOperationException("至少启用一个歌词源。");
            var lost = expiry();
            var neteasePoll = readNetease();
            var applePoll = readApple();
            if ((netease.IsChecked == true && lost <= neteasePoll) || (apple.IsChecked == true && lost <= applePoll))
                throw new InvalidOperationException("失联判定时间必须大于已启用播放器的采样间隔。");
            return playbackStore.Edit(document =>
            {
                var targetMusic = PlaybackSettingsStore.Section(document, "music");
                targetMusic["enable_netease"] = netease.IsChecked == true;
                targetMusic["enable_apple"] = apple.IsChecked == true;
                targetMusic["netease_poll_interval"] = neteasePoll;
                targetMusic["apple_poll_interval"] = applePoll;
                targetMusic["snapshot_max_age"] = lost;
                document["source_priority"] = priority.SelectedIndex == 0
                    ? new JsonArray("applemusic", "netease") : new JsonArray("netease", "applemusic");
                var targetLyrics = PlaybackSettingsStore.Section(document, "lyrics");
                targetLyrics["provider_order"] = new JsonArray(keys.Select(key => (JsonNode?)JsonValue.Create(key)).ToArray());
                targetLyrics["parallel"] = parallel.IsChecked == true;
                targetLyrics["total_timeout"] = timeout();
                var targetNetwork = PlaybackSettingsStore.Section(document, "netease");
                targetNetwork["timeout"] = request();
                targetNetwork["retries"] = (int)retries();
                var targetCache = PlaybackSettingsStore.Section(document, "cache");
                if (string.IsNullOrWhiteSpace(caching.Text)) targetCache.Remove("data_dir");
                else targetCache["data_dir"] = Path.GetFullPath(caching.Text.Trim());
            });
        };
        return grid;
    }

    private Func<double> ServiceNumber(Ui.Grid grid, string label, JsonObject? section, string key,
        double fallback, double min, double max, double step) =>
        Number(grid, label, PlaybackSettingsStore.Read(section, key, fallback), min, max, step, _ => QueueService());

    private void Integer(Ui.Grid grid, string label, int initial, int min, int max,
        Func<WallpaperSettings, int, WallpaperSettings> setter, int step = 1) =>
        Decimal(grid, label, initial, min, max, step, (s, value) => setter(s, (int)Math.Round(value)));

    private W.FrameworkElement Decimal(Ui.Grid grid, string label, double initial, double min, double max, double step,
        Func<WallpaperSettings, double, WallpaperSettings> setter)
    {
        Number(grid, label, initial, min, max, step, out var field, number =>
        {
            if (number is double value) QueueWallpaper(s => setter(s, value));
        });
        return field;
    }

    private void Option<T>(Ui.Grid grid, string label, (string Label, T Value)[] options, T selected,
        Func<WallpaperSettings, T, WallpaperSettings> setter, bool full = false)
    {
        int index = Array.FindIndex(options, option => EqualityComparer<T>.Default.Equals(option.Value, selected));
        var choice = Choice(label, options.Select(option => option.Label).ToArray(), index);
        AddField(grid, label, choice, full);
        choice.SelectionChanged += (_, _) =>
        {
            if (choice.SelectedIndex < 0) return;
            T value = options[choice.SelectedIndex].Value;
            QueueWallpaper(s => setter(s, value));
        };
    }

    private Fluent.ToggleSwitch Boolean(Ui.Grid grid, string label, bool initial, Func<WallpaperSettings, bool, WallpaperSettings> setter,
        string? hint = null)
    {
        var check = new Fluent.ToggleSwitch
        {
            Content = label, IsChecked = initial,
            MinHeight = ControlHeight, VerticalContentAlignment = W.VerticalAlignment.Center
        };
        W.Automation.AutomationProperties.SetName(check, label);
        check.ToolTip = hint;
        check.Checked += (_, _) => QueueWallpaper(s => setter(s, true));
        check.Unchecked += (_, _) => QueueWallpaper(s => setter(s, false));
        AddField(grid, "", check);
        return check;
    }

    private void ColorField(Ui.Grid grid, string label, int color, double opacity,
        Func<WallpaperSettings, int, double, WallpaperSettings> setter, double minimum = 0, double maximum = 1,
        bool wholePercent = false, Func<bool>? canEditColor = null)
    {
        var preview = new Ui.Border { CornerRadius = new W.CornerRadius(4) };
        var swatch = new Ui.Border
        {
            Width = 24, Height = 18, VerticalAlignment = W.VerticalAlignment.Center,
            Margin = new W.Thickness(0, 0, 8, 0), Child = preview,
            CornerRadius = new W.CornerRadius(5), BorderBrush = line, BorderThickness = new W.Thickness(1)
        };
        swatch.SetResourceReference(Ui.Border.BackgroundProperty, "ColorCheckerBrush");
        var hex = new Ui.TextBlock { VerticalAlignment = W.VerticalAlignment.Center };
        var percent = Text("", 12, muted);
        var colorLabel = new Ui.DockPanel();
        Ui.DockPanel.SetDock(swatch, Ui.Dock.Left);
        colorLabel.Children.Add(swatch);
        colorLabel.Children.Add(hex);
        void Refresh()
        {
            preview.Background = new Media.SolidColorBrush(ColorPickerWindow.Rgb(color)) { Opacity = opacity };
            hex.Text = ColorPickerWindow.FormatHex(color);
            percent.Text = ColorPickerWindow.FormatOpacity(opacity);
        }
        Refresh();
        var pickColor = Button(Inline(colorLabel, percent), () =>
        {
            var picker = new ColorPickerWindow(label, color, opacity, minimum, maximum, canEditColor?.Invoke() ?? true, wholePercent)
            {
                Owner = this
            };
            if (picker.ShowDialog() != true) return;
            var selected = (Color: picker.SelectedArgb, Opacity: picker.SelectedOpacity);
            (color, opacity) = selected;
            Refresh();
            QueueWallpaper(s => setter(s, selected.Color, selected.Opacity));
        });
        pickColor.HorizontalContentAlignment = W.HorizontalAlignment.Stretch;
        W.Automation.AutomationProperties.SetName(pickColor, "选择" + label + "和不透明度");
        AddField(grid, label, pickColor);
    }

    private Func<double> Number(Ui.Grid grid, string label, double initial, double min, double max, double step,
        Action<double?> changed) =>
        Number(grid, label, initial, min, max, step, out _, changed);

    private Func<double> Number(Ui.Grid grid, string label, double initial, double min, double max, double step,
        out W.FrameworkElement field, Action<double?> changed)
    {
        initial = Math.Clamp(initial, min, max);
        var slider = new Ui.Slider { Minimum = min, Maximum = max, Value = initial, TickFrequency = step,
            Style = (W.Style)FindResource("SettingsSliderStyle"),
            SmallChange = step, LargeChange = step * 5, IsSnapToTickEnabled = true,
            VerticalAlignment = W.VerticalAlignment.Center, Margin = new W.Thickness(0, 0, 9, 0) };
        var input = new Fluent.TextBox { Width = 74, MinHeight = ControlHeight, FontFamily = new Media.FontFamily("Cascadia Mono, Consolas"), Text = initial.ToString(CultureInfo.InvariantCulture),
            TextAlignment = W.TextAlignment.Right };
        W.Automation.AutomationProperties.SetName(slider, label);
        W.Automation.AutomationProperties.SetName(input, label + " 数值");
        bool updating = false;
        bool TryRead(out double value) =>
            double.TryParse(input.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            double.IsFinite(value) && value >= min && value <= max;
        slider.ValueChanged += (_, _) =>
        {
            if (updating) return;
            input.Text = slider.Value.ToString("0.##", CultureInfo.InvariantCulture);
        };
        input.TextChanged += (_, _) =>
        {
            if (!TryRead(out double value))
            {
                input.BorderBrush = error;
                changed(null);
                return;
            }
            input.ClearValue(Ui.TextBox.BorderBrushProperty);
            updating = true;
            slider.Value = value;
            updating = false;
            changed(value);
        };
        input.LostKeyboardFocus += (_, _) =>
        {
            if (!TryRead(out _)) input.Text = slider.Value.ToString(CultureInfo.InvariantCulture);
        };
        field = AddField(grid, label, Inline(slider, input));
        return () =>
        {
            if (!TryRead(out double value))
                throw new InvalidOperationException($"{label}需要在 {min:0.##} 到 {max:0.##} 之间。");
            return value;
        };
    }

    private Ui.ListBox Choice(string label, string[] options, int index)
    {
        var choice = new Ui.ListBox
        {
            Style = (W.Style)FindResource("SettingsSegmentedChoiceStyle"),
            ItemsSource = options,
            SelectedIndex = index
        };
        W.Automation.AutomationProperties.SetName(choice, label);
        choice.PreviewKeyDown += (_, e) =>
        {
            int offset = e.Key switch { Input.Key.Left => -1, Input.Key.Right => 1, _ => 0 };
            if (offset == 0 || e.KeyboardDevice.Modifiers != Input.ModifierKeys.None) return;
            int next = choice.SelectedIndex + offset;
            if (next >= 0 && next < choice.Items.Count)
            {
                choice.SelectedIndex = next;
                if (choice.ItemContainerGenerator.ContainerFromIndex(next) is Ui.ListBoxItem item)
                    item.Focus();
            }
            e.Handled = true;
        };
        return choice;
    }

    private static Ui.CheckBox Check(string label, bool value) =>
        new() { Content = label, IsChecked = value, MinHeight = ControlHeight, VerticalContentAlignment = W.VerticalAlignment.Center };

    private Ui.Button Button(object content, Action action)
    {
        var button = new Fluent.Button { Content = content, MinHeight = ControlHeight, MinWidth = 52 };
        button.Click += (_, _) => TryAction(action);
        return button;
    }

    private static Ui.Grid Fields()
    {
        var grid = new Ui.Grid();
        grid.ColumnDefinitions.Add(new Ui.ColumnDefinition());
        grid.ColumnDefinitions.Add(new Ui.ColumnDefinition());
        return grid;
    }

    private W.FrameworkElement AddField(Ui.Grid grid, string label, W.FrameworkElement element, bool full = false)
    {
        int cell = grid.Children.Count;
        int row = 0, column = 0;
        if (cell > 0)
        {
            var last = grid.Children[cell - 1];
            row = Ui.Grid.GetRow(last);
            column = Ui.Grid.GetColumn(last) + Ui.Grid.GetColumnSpan(last);
            if (column >= 2 || full) { row++; column = 0; }
        }
        while (grid.RowDefinitions.Count <= row)
            grid.RowDefinitions.Add(new Ui.RowDefinition { Height = W.GridLength.Auto });
        var group = new Ui.StackPanel { Margin = new W.Thickness(0, 1, full ? 0 : 13, 7) };
        if (label.Length > 0)
        {
            var caption = Text(label, 12, muted);
            caption.Margin = new W.Thickness(0, 0, 0, 2);
            group.Children.Add(caption);
        }
        group.Children.Add(element);
        Ui.Grid.SetRow(group, row);
        Ui.Grid.SetColumn(group, column);
        Ui.Grid.SetColumnSpan(group, full ? 2 : 1);
        grid.Children.Add(group);
        return group;
    }

    private static Ui.Grid Inline(W.FrameworkElement main, params W.FrameworkElement[] suffix)
    {
        var panel = new Ui.Grid();
        panel.ColumnDefinitions.Add(new Ui.ColumnDefinition());
        panel.Children.Add(main);
        foreach (var item in suffix)
        {
            Ui.Grid.SetColumn(item, panel.ColumnDefinitions.Count);
            panel.ColumnDefinitions.Add(new Ui.ColumnDefinition { Width = W.GridLength.Auto });
            item.Margin = new W.Thickness(6, 0, 0, 0);
            item.VerticalAlignment = W.VerticalAlignment.Center;
            panel.Children.Add(item);
        }
        return panel;
    }

    private static Ui.TextBlock Text(string value, double size, Media.Brush color) =>
        new() { Text = value, FontSize = size, Foreground = color };

    private Media.Brush ThemeBrush(string key) => (Media.Brush)FindResource(key);

    private Ui.TextBlock PathLabel(string path, string empty)
    {
        var text = Text("", 13, ink);
        text.TextTrimming = W.TextTrimming.CharacterEllipsis;
        text.VerticalAlignment = W.VerticalAlignment.Center;
        SetPath(text, path, empty);
        return text;
    }

    private static void SetPath(Ui.TextBlock label, string path, string empty)
    {
        label.Text = path.Length == 0 ? empty : path;
        label.ToolTip = path.Length == 0 ? null : path;
    }

    private string? PickFolder(string title)
    {
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = title };
        return picker.ShowDialog(this) == true ? picker.FolderName : null;
    }

    private sealed record DialogOwner(nint Handle) : System.Windows.Forms.IWin32Window;

    private void TryAction(Action action)
    {
        try
        {
            action();
            errorNotice.Visibility = W.Visibility.Collapsed;
        }
        catch (Exception ex) when (BackgroundRotation.IsImageError(ex) ||
                                   ex is InvalidOperationException or System.Text.Json.JsonException)
        {
            errorNotice.Text = ex is OutOfMemoryException ? "无法读取图片，请选择受支持的图片文件。" : ex.Message;
            errorNotice.Visibility = W.Visibility.Visible;
        }
    }
}
