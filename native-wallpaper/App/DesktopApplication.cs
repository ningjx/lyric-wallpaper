using System.Windows.Threading;
using LyricWallpaper.Platform.Windows;
using LyricWallpaper.Playback;
using LyricWallpaper.Presentation;
using LyricWallpaper.Storage;
using LyricWallpaper.Wallpaper;
using LyricWallpaper.Widgets;

namespace LyricWallpaper.App;

internal static class DesktopApplication
{
    public static RenderReport Run(WallpaperStore wallpaperStore, PlaybackSettingsStore playbackStore, WidgetStore widgetStore,
        WidgetCatalog catalog, LyricsService? worker, WallpaperSettings appearance,
        bool testing, bool openSettings, bool openEditor,
        Func<Action<RenderControls>, Action<PlaybackSnapshot?>, Action<WidgetEditorLayout>, Action<WidgetHold>, CancellationToken, RenderReport> render)
    {
        var ui = Dispatcher.CurrentDispatcher;
        DesktopTray? tray = null;
        WidgetEditorWindow? editor = null;
        Action<WidgetHold>? openFromWidget = null;
        var layoutGate = new object();
        WidgetEditorLayout pendingLayout = default;
        bool layoutQueued = false;
        var ready = new TaskCompletionSource<RenderControls>();
        var finished = new TaskCompletionSource<RenderReport>();
        using var stop = new CancellationTokenSource();
        using var wait = new DesktopWindow.MessageWait();
        // The desktop window and D3D11 context share an STA that stays out of the
        // settings window's native move/resize loop and other modal UI.
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            try
            {
                finished.SetResult(render(ready.SetResult,
                    sample => ui.BeginInvoke(new Action(() => tray?.Update(sample))),
                    PublishLayout, hold => ui.BeginInvoke(new Action(() => openFromWidget?.Invoke(hold))), stop.Token));
            }
            catch (Exception ex)
            {
                ready.TrySetException(ex);
                finished.SetException(ex);
            }
            finally { dispatcher.InvokeShutdown(); }
        }) { Name = "Desktop wallpaper renderer" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        void PublishLayout(WidgetEditorLayout layout)
        {
            lock (layoutGate)
            {
                pendingLayout = layout;
                if (layoutQueued) return;
                layoutQueued = true;
            }
            ui.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                WidgetEditorLayout latest;
                lock (layoutGate)
                {
                    latest = pendingLayout;
                    layoutQueued = false;
                }
                editor?.Place(latest);
            }));
        }

        try
        {
            var controls = ready.Task.GetAwaiter().GetResult();
            void OnRender(Action action)
            {
                if (finished.Task.IsCompleted) return;
                try { controls.Dispatcher.InvokeAsync(action, DispatcherPriority.Send).Task.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) when (finished.Task.IsCompleted) { }
            }
            void ToggleEditor()
            {
                if (editor is not null) { editor.Close(); return; }
                OpenEditor(null);
            }
            void OpenEditor(WidgetHold? hold)
            {
                if (editor is not null) return;
                WidgetDocument? widgets = null;
                OnRender(() => widgets = controls.SetWidgetEditing(true));
                if (widgets is null) return;
                try
                {
                    var overlay = new WidgetEditorWindow(widgets, hold, catalog, (next, save) =>
                    {
                        if (save) widgetStore.Save(next);
                        OnRender(() => controls.ApplyWidgets(next));
                    }, id => OnRender(() => controls.SetDragging(id)));
                    overlay.Closed += (_, _) =>
                    {
                        editor = null;
                        OnRender(() => controls.SetWidgetEditing(false));
                        tray?.RefreshWidgetEditor();
                    };
                    editor = overlay;
                    System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(overlay);
                    overlay.Show();
                    overlay.Activate();
                    tray?.RefreshWidgetEditor();
                }
                catch
                {
                    editor?.Close();
                    editor = null;
                    OnRender(() => controls.SetWidgetEditing(false));
                    throw;
                }
            }
            openFromWidget = hold =>
            {
                try { OpenEditor(hold); }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "无法打开控件编辑", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            using var desktopTray = worker is null ? null : new DesktopTray(wallpaperStore, playbackStore, worker,
                () => appearance,
                updated =>
                {
                    OnRender(() => controls.ApplyAppearance(updated));
                    appearance = updated;
                },
                () => testing,
                enabled =>
                {
                    OnRender(() => controls.SetTestMode(enabled));
                    testing = enabled;
                },
                () => editor is not null, ToggleEditor);
            tray = desktopTray;
            tray?.Update(worker?.Latest);
            if (openSettings) tray?.Open();
            if (openEditor)
            {
                ToggleEditor();
                tray?.RefreshWidgetEditor();
            }
            var renderFinished = ((IAsyncResult)finished.Task).AsyncWaitHandle;
            while (!finished.Task.IsCompleted)
            {
                Application.DoEvents();
                if (tray?.ExitRequested == true) break;
                wait.Until(double.PositiveInfinity, renderFinished);
            }
        }
        finally
        {
            tray = null;
            editor?.Close();
            stop.Cancel();
            thread.Join();
        }
        return finished.Task.GetAwaiter().GetResult();
    }
}
