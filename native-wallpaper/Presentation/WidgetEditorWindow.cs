using System.Windows.Interop;
using System.Windows.Threading;
using W = System.Windows;
using Controls = System.Windows.Controls;
using Primitives = System.Windows.Controls.Primitives;
using Input = System.Windows.Input;
using Media = System.Windows.Media;
using Shapes = System.Windows.Shapes;
using Fluent = Wpf.Ui.Controls;
using LyricWallpaper.Platform.Windows;
using LyricWallpaper.Scene;
using LyricWallpaper.Widgets;

namespace LyricWallpaper.Presentation;

internal sealed class WidgetEditorWindow : W.Window
{
    private static readonly Media.Brush Accent = Brush(0xFF0067C0);
    private static readonly Media.Brush Surface = Brush(0xFFF6F8FB);
    private static readonly Media.Brush BorderColor = Brush(0xFFD5DCE4);
    private static readonly Media.Brush Ink = Brush(0xFF20252D);
    private const double HandleSize = 11, RemoveSize = 26;

    private readonly WidgetCatalog catalog;
    private readonly WidgetEditSession session;
    private readonly Action<Guid?> setDragging;
    private readonly Controls.Canvas canvas = new() { Focusable = true, ClipToBounds = true };
    private readonly Controls.Canvas inputLayer = new() { Background = Brush(0x02000000), Cursor = Input.Cursors.Hand };
    private readonly Controls.Canvas selection = new();
    private readonly Controls.Border outline = new()
    {
        BorderBrush = Accent, BorderThickness = new W.Thickness(2), IsHitTestVisible = false, SnapsToDevicePixels = true
    };
    private readonly Primitives.Thumb[] handles = new Primitives.Thumb[4];
    private readonly Shapes.Line guideX = Guide(), guideY = Guide();
    private readonly Controls.Border propertyPanel;
    private readonly WidgetDock dock;
    private readonly WindowInputRegion inputRegion = new();
    private readonly Controls.StackPanel properties = new() { Visibility = W.Visibility.Collapsed };
    private readonly Controls.Slider scale;
    private readonly Controls.Button remove = new() { Width = RemoveSize, Height = RemoveSize, ToolTip = "删除控件" };
    private readonly Fluent.ToggleSwitch idleOnly = WidgetPropertyControls.Toggle("仅闲时显示");
    private readonly Controls.ContentControl inspectorHost = new();
    private IWidgetInspector? inspector;
    private string? inspectorType;
    private readonly DispatcherTimer iconTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private WidgetEditSnapshot State => session.State;
    private WidgetHold? continuedHold;
    private WidgetEditorLayout? layout;
    private WidgetEditorMouseInput? mouseInput;
    private DispatcherOperation? heldPointerUpdate;
    private (Point Position, bool Released) heldPointer;
    private Rectangle[] icons = [];
    private nint iconOwner, hwnd;
    private bool queryingIcons, closed, updatingProperties;
    private Gesture? gesture;
    private (WidgetEditSnapshot Before, Primitives.Thumb Thumb)? propertyDrag;
    private W.Rect propertyBounds = W.Rect.Empty;
    private double? snapX, snapY;
    private bool IsInteracting => gesture is not null || propertyDrag is not null;

    public WidgetEditorWindow(WidgetDocument document, WidgetHold? hold, WidgetCatalog catalog,
        Action<WidgetDocument, bool> apply, Action<Guid?> setDragging)
    {
        session = new(document, hold?.Id, apply);
        continuedHold = hold;
        this.catalog = catalog;
        this.setDragging = setDragging;
        Title = "编辑桌面控件";
        WindowStyle = W.WindowStyle.None;
        ResizeMode = W.ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Media.Brushes.Transparent;
        ShowInTaskbar = false;
        WindowStartupLocation = W.WindowStartupLocation.Manual;
        Left = Top = 0;
        Width = 480;
        Height = 320;
        WindowState = W.WindowState.Maximized;
        FontFamily = SettingsTheme.FontFamily;
        FontSize = SettingsTheme.FontSize;
        Foreground = Ink;
        UseLayoutRounding = true;
        SettingsTheme.Apply(this);
        Content = canvas;

        canvas.Children.Add(guideX);
        canvas.Children.Add(guideY);
        canvas.Children.Add(inputLayer);
        canvas.Children.Add(selection);
        selection.Children.Add(outline);
        selection.Visibility = W.Visibility.Collapsed;
        inputLayer.PreviewMouseLeftButtonDown += (_, e) => PointerDown(e, -1);
        inputLayer.MouseRightButtonUp += (_, e) =>
        {
            if (Hit(canvas.PointToScreen(e.GetPosition(canvas))) is not { } card) return;
            Select(card.Id);
            e.Handled = true;
        };
        for (int i = 0; i < handles.Length; i++)
        {
            int corner = i;
            var grip = new W.FrameworkElementFactory(typeof(Controls.Border));
            grip.SetValue(Controls.Border.BackgroundProperty, Media.Brushes.White);
            grip.SetValue(Controls.Border.BorderBrushProperty, Accent);
            grip.SetValue(Controls.Border.BorderThicknessProperty, new W.Thickness(2));
            grip.SetValue(Controls.Border.CornerRadiusProperty, new W.CornerRadius(2));
            var handle = new Primitives.Thumb
            {
                Width = HandleSize, Height = HandleSize, Focusable = false,
                Template = new Controls.ControlTemplate(typeof(Primitives.Thumb)) { VisualTree = grip },
                Cursor = corner is 0 or 3 ? Input.Cursors.SizeNWSE : Input.Cursors.SizeNESW
            };
            W.Automation.AutomationProperties.SetAutomationId(handle, $"ClockResize{corner}");
            W.Automation.AutomationProperties.SetName(handle, "缩放时钟");
            handle.PreviewMouseLeftButtonDown += (_, e) => PointerDown(e, corner);
            selection.Children.Add(handles[i] = handle);
        }
        remove.SetResourceReference(W.FrameworkElement.StyleProperty, "WidgetRemoveStyle");
        W.Automation.AutomationProperties.SetName(remove, "删除控件");
        remove.Click += (_, _) => RemoveSelected();
        selection.Children.Add(remove);

        var rows = new Controls.StackPanel();
        rows.Children.Add(properties);
        if (document.ReadOnlyReason is { } reason)
            rows.Children.Add(new Controls.TextBlock { Text = reason, TextWrapping = W.TextWrapping.Wrap });

        properties.Children.Add(WidgetPropertyControls.Range("缩放", WidgetPlacement.MinScale * 100,
            WidgetPlacement.MaxScale * 100, "{0:0}%", out scale));
        scale.ValueChanged += (_, e) =>
        {
            if (!updatingProperties && session.Selected is { } item)
                ChangeProperty(session.WithPlacement(item.Placement with { Scale = e.NewValue / 100 }));
        };
        properties.Children.Add(inspectorHost);
        properties.Children.Add(idleOnly);
        idleOnly.Margin = new W.Thickness(0, 3, 0, 0);
        idleOnly.Checked += (_, _) => SetVisibility(WidgetVisibility.Idle);
        idleOnly.Unchecked += (_, _) => SetVisibility(WidgetVisibility.Always);

        propertyPanel = new Controls.Border
        {
            Child = rows, Background = Surface, BorderBrush = BorderColor, BorderThickness = new W.Thickness(1),
            CornerRadius = new W.CornerRadius(10), Padding = new W.Thickness(10, 7, 10, 7), Visibility = W.Visibility.Collapsed
        };
        propertyPanel.SizeChanged += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(LayoutParts));
        propertyPanel.AddHandler(Primitives.Thumb.DragStartedEvent, new Primitives.DragStartedEventHandler((_, e) =>
        {
            if (!IsInteracting && e.OriginalSource is Primitives.Thumb thumb) propertyDrag = (State, thumb);
        }), true);
        propertyPanel.AddHandler(Primitives.Thumb.DragCompletedEvent, new Primitives.DragCompletedEventHandler((_, e) =>
        {
            if (ReferenceEquals(propertyDrag?.Thumb, e.OriginalSource)) FinishPropertyDrag(e.Canceled);
        }), true);
        canvas.Children.Add(propertyPanel);
        dock = new WidgetDock(catalog, session.CanEdit, AddWidget, LayoutParts);
        canvas.Children.Add(dock.View);
        SourceInitialized += (_, _) => hwnd = new WindowInteropHelper(this).Handle;
        Loaded += (_, _) =>
        {
            LayoutParts();
            Input.Keyboard.Focus(canvas);
            mouseInput ??= new(IsOutsideEditor, CloseAfterOutsideClick);
            ContinueHold();
        };
        SizeChanged += (_, _) => LayoutParts();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(new Action(LayoutParts));
        Activated += (_, _) =>
        {
            if (Input.Keyboard.FocusedElement is null || !IsKeyboardFocusWithin) Input.Keyboard.Focus(canvas);
            CaptureContinuedGesture();
            ContinueHold();
        };
        Deactivated += (_, _) => CancelInteraction();
        PreviewKeyDown += OnKey;
        canvas.PreviewMouseMove += (_, e) =>
        {
            if (gesture is { Captured: true }) Move(canvas.PointToScreen(e.GetPosition(canvas)));
        };
        canvas.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (gesture is null) return;
            Move(canvas.PointToScreen(e.GetPosition(canvas)));
            FinishGesture();
            e.Handled = true;
        };
        canvas.LostMouseCapture += (_, _) => { if (gesture is { Captured: true }) CancelGesture(); };
        Closed += (_, _) =>
        {
            mouseInput?.Dispose();
            closed = true;
            CancelInteraction();
            iconTimer.Stop();
        };
        iconTimer.Tick += async (_, _) => await RefreshIconsAsync();
        iconTimer.Start();
        UpdateProperties();
    }

    public void Place(WidgetEditorLayout next)
    {
        bool displayChanged = layout?.Display != next.Display;
        if (layout == next) return;
        if (displayChanged) CancelInteraction();
        layout = next;
        if (next.IconView != iconOwner)
        {
            iconOwner = next.IconView;
            icons = [];
            _ = RefreshIconsAsync();
        }
        LayoutParts();
        ContinueHold();
    }

    private void LayoutParts()
    {
        if (closed || layout is not { } value || W.PresentationSource.FromVisual(canvas) is null) return;
        WidgetLayout? active = LayoutFor(State.Selected);
        var card = active is { } selected ? ToLocal(selected.Bounds) : W.Rect.Empty;
        var work = ToLocal(value.WorkArea);
        bool canShowProperties = active.HasValue && session.CanEdit;
        selection.Visibility = active.HasValue ? W.Visibility.Visible : W.Visibility.Collapsed;
        remove.Visibility = canShowProperties ? W.Visibility.Visible : W.Visibility.Collapsed;
        propertyPanel.Visibility = canShowProperties || State.Document.ReadOnlyReason is not null ? W.Visibility.Visible : W.Visibility.Collapsed;
        inputLayer.Width = selection.Width = canvas.ActualWidth;
        inputLayer.Height = selection.Height = canvas.ActualHeight;
        var hits = new List<Rectangle>();
        foreach (var item in value.Items)
        {
            if (!State.Document.Items.Any(widget => widget.Id == item.Id)) continue;
            var box = Rectangle.Ceiling(item.Bounds);
            if (item.Id == State.Selected)
            {
                int padding = (int)Math.Ceiling(HandleSize / DeviceToDip.M11);
                box.Inflate(padding, padding);
            }
            hits.Add(box);
        }
        if (active is { } selectedClock)
        {
            Controls.Canvas.SetLeft(outline, card.Left);
            Controls.Canvas.SetTop(outline, card.Top);
            outline.Width = card.Width;
            outline.Height = card.Height;
            outline.CornerRadius = new W.CornerRadius(Math.Max(0, selectedClock.Radius * DeviceToDip.M11));
            for (int i = 0; i < handles.Length; i++)
            {
                Controls.Canvas.SetLeft(handles[i], ((i & 1) != 0 ? card.Right : card.Left) - HandleSize / 2);
                Controls.Canvas.SetTop(handles[i], ((i & 2) != 0 ? card.Bottom : card.Top) - HandleSize / 2);
            }
        }

        dock.Arrange(work);
        var overlays = new List<Rectangle> { ToScreen(dock.Bounds) };
        var removeBounds = W.Rect.Empty;
        if (canShowProperties)
        {
            removeBounds = PlaceRemove(card, work);
            overlays.Add(ToScreen(removeBounds));
        }
        if (propertyPanel.Visibility == W.Visibility.Visible)
        {
            // Keep the slider under the pointer when its preview changes the card's dimensions.
            if (propertyDrag is null || propertyBounds.IsEmpty)
                propertyBounds = PlacePropertyPanel(card, work, removeBounds);
            overlays.Add(ToScreen(propertyBounds));
        }

        ShowGuide(guideX, snapX, work, true);
        ShowGuide(guideY, snapY, work, false);
        var area = value.WorkArea;
        if (snapX is { } sx)
        {
            int line = (int)Math.Round(sx);
            overlays.Add(Rectangle.FromLTRB(line - 1, area.Top, line + 2, area.Bottom));
        }
        if (snapY is { } sy)
        {
            int line = (int)Math.Round(sy);
            overlays.Add(Rectangle.FromLTRB(area.Left, line - 1, area.Right, line + 2));
        }
        inputRegion.Update(hwnd, hits.ToArray(), icons, overlays.ToArray());
    }

    private W.Rect PlaceRemove(W.Rect card, W.Rect work)
    {
        double y = card.Top - RemoveSize - 8;
        if (y < work.Top) y = card.Top + HandleSize / 2 + 5;
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - RemoveSize));
        double right = dock.Bounds.Top < y + RemoveSize && dock.Bounds.Bottom > y ? dock.Bounds.Left - 4 : work.Right;
        double x = Math.Clamp(card.Right - RemoveSize, work.Left, Math.Max(work.Left, right - RemoveSize));
        Controls.Canvas.SetLeft(remove, x);
        Controls.Canvas.SetTop(remove, y);
        return new(x, y, RemoveSize, RemoveSize);
    }

    private W.Rect PlacePropertyPanel(W.Rect card, W.Rect work, W.Rect removeBounds)
    {
        const double gap = 12;
        propertyPanel.Width = Math.Min(264, Math.Max(0, work.Width - gap * 2));
        propertyPanel.Measure(new W.Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = propertyPanel.DesiredSize;
        double right = dock.Bounds.Left - gap;
        if (right - work.Left < size.Width + gap * 2) right = work.Right;
        double minX = work.Left + gap, maxX = Math.Max(minX, right - size.Width - gap);
        double center = card.IsEmpty ? work.Left + work.Width / 2 : card.Left + card.Width / 2;
        double x = Math.Clamp(center - size.Width / 2, minX, maxX);
        double y = card.IsEmpty ? work.Top + gap : card.Bottom + gap;
        if (!card.IsEmpty && y + size.Height > work.Bottom - gap)
        {
            double top = removeBounds.IsEmpty ? card.Top : Math.Min(card.Top, removeBounds.Top);
            y = top - size.Height - gap;
            if (y < work.Top + gap)
            {
                x = card.Right + gap;
                if (x > maxX) x = card.Left - size.Width - gap;
                x = Math.Clamp(x, minX, maxX);
                y = Math.Clamp(card.Top, work.Top + gap, Math.Max(work.Top + gap, work.Bottom - size.Height - gap));
            }
        }
        Controls.Canvas.SetLeft(propertyPanel, x);
        Controls.Canvas.SetTop(propertyPanel, y);
        return new(new W.Point(x, y), size);
    }

    private Rectangle ToScreen(W.Rect bounds)
    {
        var topLeft = canvas.PointToScreen(bounds.TopLeft);
        var bottomRight = canvas.PointToScreen(bounds.BottomRight);
        return Rectangle.FromLTRB((int)Math.Floor(topLeft.X), (int)Math.Floor(topLeft.Y),
            (int)Math.Ceiling(bottomRight.X), (int)Math.Ceiling(bottomRight.Y));
    }

    private async Task RefreshIconsAsync()
    {
        if (queryingIcons || iconOwner == 0 || closed) return;
        queryingIcons = true;
        nint owner = iconOwner;
        try
        {
            var next = await Task.Run(() => WidgetInputWindow.ReadDesktopIconBounds(owner));
            if (!closed && owner == iconOwner && next is not null && !icons.SequenceEqual(next))
            {
                icons = next;
                LayoutParts();
            }
        }
        finally { queryingIcons = false; }
    }

    private bool IsOutsideEditor(Point point) => !IsInteracting && layout is { } value &&
        !WidgetEditorMouseInput.HitsAppControls(point, value.WallpaperView);

    private void CloseAfterOutsideClick()
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!closed && !IsInteracting) Close();
        }, DispatcherPriority.Background);
    }

    private void PointerDown(Input.MouseButtonEventArgs e, int corner)
    {
        if (layout is null || IsInteracting) return;
        Input.Keyboard.Focus(canvas);
        e.Handled = true;
        W.Point pointer = canvas.PointToScreen(e.GetPosition(canvas));
        var target = corner < 0 ? Hit(pointer) : LayoutFor(State.Selected);
        if (target is not { } card) return;
        Select(card.Id);
        BeginGesture(card, pointer, corner);
    }

    private WidgetLayout? Hit(W.Point pointer)
    {
        if (layout is not { } value) return null;
        var point = new Point((int)pointer.X, (int)pointer.Y);
        for (int i = value.Items.Length - 1; i >= 0; i--)
        {
            var card = value.Items[i];
            if (card.Contains(point) && State.Document.Items.Any(widget => widget.Id == card.Id)) return card;
        }
        return null;
    }

    private WidgetLayout? LayoutFor(Guid? id)
    {
        if (id is null || layout is not { } value) return null;
        foreach (var card in value.Items)
            if (card.Id == id) return card;
        return null;
    }

    private void Select(Guid id)
    {
        if (State.Selected == id) return;
        session.Select(id);
        UpdateProperties();
        LayoutParts();
    }

    private void ContinueHold()
    {
        if (continuedHold is not { } hold || mouseInput is null || LayoutFor(hold.Id) is not { } card) return;
        continuedHold = null;
        if (!IsInteracting && State.Selected == hold.Id && WidgetInputWindow.IsLeftButtonDown)
            BeginGesture(card, new W.Point(hold.Pointer.X, hold.Pointer.Y), -1, continued: true);
    }

    private void BeginGesture(WidgetLayout card, W.Point pointer, int corner, bool continued = false)
    {
        if (!session.CanEdit || session.Selected is not { } item) return;
        bool captured = (!continued || IsActive) && Input.Mouse.Capture(canvas);
        // Windows can deny activation during the handoff from the desktop HWND.
        if (!captured && (!continued || mouseInput?.FollowHeldPress(QueueHeldPointer) != true)) return;
        gesture = new(State, item.Placement, card.Bounds, pointer, corner, captured);
        setDragging(card.Id);
        if (!continued) return;
        FollowCurrentPointer();
        if (!captured) Dispatcher.InvokeAsync(CaptureContinuedGesture, DispatcherPriority.Input);
    }

    private void CaptureContinuedGesture()
    {
        if (!IsActive || gesture is not { Captured: false } active ||
            !WidgetInputWindow.IsLeftButtonDown || !Input.Mouse.Capture(canvas)) return;
        gesture = active with { Captured = true };
        StopFollowingHeldPointer();
        FollowCurrentPointer();
    }

    private void FollowCurrentPointer()
    {
        var position = System.Windows.Forms.Cursor.Position;
        Move(new W.Point(position.X, position.Y));
        if (!WidgetInputWindow.IsLeftButtonDown) FinishGesture();
    }

    private void QueueHeldPointer(Point position, bool released)
    {
        heldPointer = (position, released);
        // Coalesce motion and leave the native hook before applying the preview to the render thread.
        heldPointerUpdate ??= Dispatcher.InvokeAsync(ApplyHeldPointer, DispatcherPriority.Input);
    }

    private void ApplyHeldPointer()
    {
        heldPointerUpdate = null;
        if (gesture is not { Captured: false }) return;
        var (position, released) = heldPointer;
        Move(new W.Point(position.X, position.Y));
        if (released) FinishGesture();
    }

    private void Move(W.Point pointer)
    {
        if (gesture is not { } active || layout is not { } value) return;
        W.Vector delta = pointer - active.Pointer;
        var box = active.Bounds;
        double x = box.Left + box.Width / 2, y = box.Top + box.Height / 2;
        double scale = active.Start.Scale;
        snapX = snapY = null;
        if (active.Corner < 0)
        {
            if (delta.LengthSquared < 4 && session.Selected?.Placement == active.Start) return;
            double threshold = 7 / DeviceToDip.M11;
            x = Snap(x + delta.X, box.Width, value.WorkArea.Left, value.WorkArea.Right, threshold, out snapX);
            y = Snap(y + delta.Y, box.Height, value.WorkArea.Top, value.WorkArea.Bottom, threshold, out snapY);
        }
        else
        {
            double dx = (active.Corner & 1) != 0 ? box.Width : -box.Width;
            double dy = (active.Corner & 2) != 0 ? box.Height : -box.Height;
            double factor = 1 + (dx * delta.X + dy * delta.Y) / (dx * dx + dy * dy);
            scale = Math.Clamp(active.Start.Scale * factor, WidgetPlacement.MinScale, WidgetPlacement.MaxScale);
            factor = scale / active.Start.Scale;
            x += dx * (factor - 1) / 2;
            y += dy * (factor - 1) / 2;
        }
        float factorFromStart = (float)(scale / active.Start.Scale);
        var position = PositionAt(x, y, value.Display, new SizeF(box.Width * factorFromStart, box.Height * factorFromStart));
        Preview(session.WithPlacement(active.Start with { Scale = scale, Position = position }));
        LayoutParts();
    }

    private static double Snap(double center, double size, double start, double end, double threshold, out double? guide)
    {
        double middle = (start + end) / 2;
        if (Math.Abs(center - middle) < threshold) { guide = middle; return middle; }
        if (Math.Abs(center - size / 2 - start) < threshold) { guide = start; return start + size / 2; }
        if (Math.Abs(center + size / 2 - end) < threshold) { guide = end; return end - size / 2; }
        guide = null;
        return center;
    }

    private void FinishGesture()
    {
        if (gesture is not { } active) return;
        var result = State;
        ReleaseGesture();
        Commit(active.Before, result);
    }

    private void CancelGesture()
    {
        if (gesture is not { } active) return;
        ReleaseGesture();
        Preview(active.Before);
    }

    private void FinishPropertyDrag(bool canceled)
    {
        if (propertyDrag is not { } drag) return;
        propertyDrag = null;
        if (canceled)
        {
            Preview(drag.Before);
            LayoutParts();
        }
        else Commit(drag.Before, State);
    }

    private void CancelInteraction()
    {
        if (propertyDrag is { } drag)
        {
            FinishPropertyDrag(canceled: true);
            drag.Thumb.CancelDrag();
        }
        CancelGesture();
    }

    private void ReleaseGesture()
    {
        gesture = null;
        StopFollowingHeldPointer();
        setDragging(null);
        snapX = snapY = null;
        if (Input.Mouse.Captured == canvas) Input.Mouse.Capture(null);
        LayoutParts();
    }

    private void StopFollowingHeldPointer()
    {
        mouseInput?.StopFollowingHeldPress();
        heldPointerUpdate?.Abort();
        heldPointerUpdate = null;
    }

    private void OnKey(object sender, Input.KeyEventArgs e)
    {
        var modifiers = Input.Keyboard.Modifiers;
        switch (e.Key)
        {
            case Input.Key.Escape:
                if (IsInteracting) CancelInteraction();
                else Close();
                break;
            case Input.Key.Delete when !IsInteracting:
                RemoveSelected();
                break;
            case Input.Key.Tab when !IsInteracting && modifiers.HasFlag(Input.ModifierKeys.Control):
                SelectAdjacent(modifiers.HasFlag(Input.ModifierKeys.Shift) ? -1 : 1);
                break;
            case Input.Key.Z when modifiers == Input.ModifierKeys.Control:
                if (!IsInteracting) Edit(session.Undo);
                break;
            case Input.Key.Y when modifiers == Input.ModifierKeys.Control:
            case Input.Key.Z when modifiers == (Input.ModifierKeys.Control | Input.ModifierKeys.Shift):
                if (!IsInteracting) Edit(session.Redo);
                break;
            case Input.Key.Left or Input.Key.Right or Input.Key.Up or Input.Key.Down
                when !IsInteracting && Input.Keyboard.FocusedElement is not (Controls.Slider or Controls.RadioButton) &&
                    session.Selected is { } item && layout is { } value &&
                    LayoutFor(State.Selected) is { } card && modifiers is Input.ModifierKeys.None or Input.ModifierKeys.Shift:
                int step = modifiers == Input.ModifierKeys.Shift ? 10 : 1;
                double x = value.Display.Left + item.Placement.Position.X * value.Display.Width;
                double y = value.Display.Top + item.Placement.Position.Y * value.Display.Height;
                var position = PositionAt(x + (e.Key == Input.Key.Left ? -step : e.Key == Input.Key.Right ? step : 0),
                    y + (e.Key == Input.Key.Up ? -step : e.Key == Input.Key.Down ? step : 0), value.Display, card.Bounds.Size);
                Commit(State, session.WithPlacement(item.Placement with { Position = position }));
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private static WidgetPosition PositionAt(double x, double y, Rectangle display, SizeF content)
    {
        float halfX = Math.Min(content.Width, display.Width) / 2, halfY = Math.Min(content.Height, display.Height) / 2;
        return new((float)Math.Clamp(x - display.Left, halfX, display.Width - halfX) / display.Width,
            (float)Math.Clamp(y - display.Top, halfY, display.Height - halfY) / display.Height);
    }

    private void SetVisibility(WidgetVisibility visibility)
    {
        if (!updatingProperties) ChangeProperty(session.WithVisibility(visibility));
    }

    private void ApplyOptions(WidgetOptions options)
    {
        if (session.Selected is not { } item || catalog.Find(item.Type) is not { } definition) return;
        ChangeProperty(session.WithOptions(definition.NormalizeOptions(options)));
    }

    private void ChangeProperty(WidgetEditSnapshot next)
    {
        if (propertyDrag is not null) Preview(next);
        else Commit(State, next);
    }

    private void AddWidget(WidgetDefinition definition)
    {
        if (IsInteracting || !session.CanEdit || layout is not { } value || dock.Bounds.IsEmpty) return;
        var occupied = new List<Rectangle>(icons);
        occupied.AddRange(value.Items.Select(item => Rectangle.Ceiling(item.Bounds)));
        foreach (var item in State.Document.Items)
        {
            if (value.Items.Any(visual => visual.Id == item.Id) || catalog.Find(item.Type) is not { } known) continue;
            var size = known.EstimateSize(value.Display);
            var position = item.Placement.Position;
            occupied.Add(new(value.Display.X + (int)(position.X * value.Display.Width) - size.Width / 2,
                value.Display.Y + (int)(position.Y * value.Display.Height) - size.Height / 2, size.Width, size.Height));
        }
        var next = WidgetPlacementPolicy.Find(value.Display, value.WorkArea, ToScreen(dock.Bounds),
            definition.EstimateSize(value.Display), occupied, Math.Max(1, (int)Math.Ceiling(12 / DeviceToDip.M11)));
        Edit(() => session.Add(catalog.Create(definition, next)));
        Input.Keyboard.Focus(canvas);
    }

    private void RemoveSelected()
    {
        if (IsInteracting) return;
        if (Edit(session.RemoveSelected)) Input.Keyboard.Focus(canvas);
    }

    private void SelectAdjacent(int offset)
    {
        session.SelectAdjacent(offset);
        UpdateProperties();
        LayoutParts();
    }

    private void Preview(WidgetEditSnapshot next)
    {
        if (State == next) return;
        session.Preview(next);
        UpdateProperties();
    }

    private void Commit(WidgetEditSnapshot before, WidgetEditSnapshot next)
    {
        if (Edit(() => session.Commit(before, next))) return;
        session.Preview(before);
        UpdateProperties();
        LayoutParts();
    }

    private bool Edit(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            W.MessageBox.Show(this, ex.Message, "无法保存控件设置", W.MessageBoxButton.OK, W.MessageBoxImage.Warning);
            return false;
        }
        UpdateProperties();
        LayoutParts();
        return true;
    }

    private void UpdateProperties()
    {
        var selected = session.Selected;
        properties.Visibility = selected is not null && session.CanEdit ? W.Visibility.Visible : W.Visibility.Collapsed;

        if (selected?.Type != inspectorType)
        {
            inspectorType = selected?.Type;
            inspector = inspectorType is null ? null : WidgetInspectors.Create(inspectorType, ApplyOptions);
            inspectorHost.Content = inspector?.View;
        }
        if (selected is null) return;
        updatingProperties = true;
        try
        {
            scale.Value = selected.Placement.Scale * 100;
            idleOnly.IsChecked = selected.Visibility == WidgetVisibility.Idle;
            inspector?.Display(selected.Options);
        }
        finally { updatingProperties = false; }
    }

    private Media.Matrix DeviceToDip => W.PresentationSource.FromVisual(canvas)?.CompositionTarget?.TransformFromDevice ?? Media.Matrix.Identity;

    private W.Rect ToLocal(RectangleF value)
    {
        return new(canvas.PointFromScreen(new W.Point(value.Left, value.Top)),
            canvas.PointFromScreen(new W.Point(value.Right, value.Bottom)));
    }

    private void ShowGuide(Shapes.Line line, double? screen, W.Rect area, bool vertical)
    {
        line.Visibility = screen is null ? W.Visibility.Collapsed : W.Visibility.Visible;
        if (screen is not { } position) return;
        var local = canvas.PointFromScreen(new W.Point(position, position));
        double coordinate = vertical ? local.X : local.Y;
        (line.X1, line.X2, line.Y1, line.Y2) = vertical
            ? (coordinate, coordinate, area.Top, area.Bottom) : (area.Left, area.Right, coordinate, coordinate);
    }

    private static Shapes.Line Guide() => new()
    {
        Stroke = Accent, StrokeThickness = 1, StrokeDashArray = [3, 3], Opacity = .66,
        IsHitTestVisible = false, Visibility = W.Visibility.Collapsed
    };
    private static Media.SolidColorBrush Brush(uint argb)
    {
        var brush = new Media.SolidColorBrush(Media.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        brush.Freeze();
        return brush;
    }

    private sealed record Gesture(WidgetEditSnapshot Before, WidgetPlacement Start, RectangleF Bounds, W.Point Pointer, int Corner, bool Captured);
}
