using System.Windows.Threading;
using LyricWallpaper.Wallpaper;
using LyricWallpaper.Widgets;

namespace LyricWallpaper.App;

internal sealed record RenderControls(Dispatcher Dispatcher, Action<WallpaperSettings> ApplyAppearance, Action<bool> SetTestMode,
        Func<bool, WidgetDocument> SetWidgetEditing, Action<WidgetDocument> ApplyWidgets, Action<Guid?> SetDragging,
        Action<bool> ShowDockPreviews);
