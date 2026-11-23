using W = System.Windows;
using Controls = System.Windows.Controls;
using Input = System.Windows.Input;
using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;
using Fluent = Wpf.Ui.Controls;
using LyricWallpaper.Scene;
using LyricWallpaper.Widgets;

namespace LyricWallpaper.Presentation;

internal sealed class WidgetDock
{
    private readonly Controls.Grid expanded = new();
    private readonly Fluent.Button expand;
    private readonly Dictionary<string, Controls.Border> previews = new(StringComparer.Ordinal);
    private bool isExpanded = true;

    public Controls.Border View { get; }
    public W.Rect Bounds { get; private set; } = W.Rect.Empty;

    public WidgetDock(WidgetCatalog catalog, bool canAdd, Action<WidgetDefinition> add, Action<bool> changed)
    {
        View = new Controls.Border
        {
            Background = Brush(0xFFF7F9FC), BorderBrush = Brush(0xFFD5DEE8),
            BorderThickness = new W.Thickness(1, 1, 0, 1),
            CornerRadius = new W.CornerRadius(14, 0, 0, 14),
            SnapsToDevicePixels = true, ClipToBounds = true, Visibility = W.Visibility.Collapsed
        };
        expanded.Margin = new W.Thickness(12, 10, 12, 10);
        expanded.RowDefinitions.Add(new() { Height = W.GridLength.Auto });
        expanded.RowDefinitions.Add(new() { Height = new W.GridLength(1, W.GridUnitType.Star) });
        var header = new Controls.DockPanel { Margin = new W.Thickness(2, 0, 0, 10) };
        var collapse = ToggleButton("收起控件栏", Fluent.SymbolRegular.ChevronRight20, () => SetExpanded(false));
        Controls.DockPanel.SetDock(collapse, Controls.Dock.Right);
        header.Children.Add(collapse);
        header.Children.Add(new Controls.TextBlock
        {
            Text = "控件", FontSize = 17, FontWeight = W.FontWeights.SemiBold, VerticalAlignment = W.VerticalAlignment.Center
        });
        expanded.Children.Add(header);
        var list = new Controls.StackPanel();
        foreach (var definition in catalog.Definitions)
            list.Children.Add(Card(definition, canAdd, () => add(definition)));
        var library = new Controls.ScrollViewer
        {
            Content = list, HorizontalScrollBarVisibility = Controls.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Controls.ScrollBarVisibility.Auto
        };
        Controls.Grid.SetRow(library, 1);
        expanded.Children.Add(library);
        expand = ToggleButton("展开控件栏", Fluent.SymbolRegular.ChevronLeft20, () => SetExpanded(true));
        View.Child = expanded;

        void SetExpanded(bool value)
        {
            isExpanded = value;
            View.Child = value ? expanded : expand;
            changed(value);
        }
    }

    public void Arrange(W.Rect work)
    {
        double width = Math.Min(work.Width, isExpanded ? 236 : 34);
        double height = isExpanded ? work.Height : Math.Min(70, work.Height);
        double top = isExpanded ? work.Top : work.Top + (work.Height - height) / 2;
        Bounds = new(work.Right - width, top, width, height);
        View.Width = width;
        View.Height = height;
        Controls.Canvas.SetLeft(View, Bounds.Left);
        Controls.Canvas.SetTop(View, Bounds.Top);
        View.Visibility = W.Visibility.Visible;
    }

    public void ShowPreviews(IReadOnlyList<WidgetPreviewFrame> frames)
    {
        foreach (var frame in frames)
        {
            if (!previews.TryGetValue(frame.Type, out var tile)) continue;
            var bitmap = Imaging.BitmapSource.Create(frame.Width, frame.Height, 96, 96, Media.PixelFormats.Bgra32,
                null, frame.Pixels, frame.Width * 4);
            bitmap.Freeze();
            var brush = new Media.ImageBrush(bitmap) { Stretch = Media.Stretch.UniformToFill };
            brush.Freeze();
            tile.Background = brush;
        }
    }

    private Fluent.Button Card(WidgetDefinition item, bool enabled, Action click)
    {
        var tile = new Controls.Border
        {
            Height = 112, CornerRadius = new W.CornerRadius(6), Background = Brush(0xFF1E2937), SnapsToDevicePixels = true
        };
        previews.Add(item.Type, tile);
        var content = new Controls.StackPanel();
        content.Children.Add(tile);
        var row = new Controls.DockPanel { Margin = new W.Thickness(3, 6, 3, 1) };
        var plus = new Fluent.SymbolIcon
        {
            Symbol = Fluent.SymbolRegular.Add16, FontSize = 16, VerticalAlignment = W.VerticalAlignment.Center,
            Foreground = Brush(0xFF526176)
        };
        Controls.DockPanel.SetDock(plus, Controls.Dock.Right);
        row.Children.Add(plus);
        row.Children.Add(new Controls.TextBlock
        {
            Text = item.Title, FontWeight = W.FontWeights.SemiBold, VerticalAlignment = W.VerticalAlignment.Center
        });
        content.Children.Add(row);
        var button = new Fluent.Button
        {
            Content = content, Padding = new W.Thickness(5), IsEnabled = enabled, Cursor = Input.Cursors.Hand,
            HorizontalAlignment = W.HorizontalAlignment.Stretch, HorizontalContentAlignment = W.HorizontalAlignment.Stretch,
            Appearance = Fluent.ControlAppearance.Secondary, Margin = new W.Thickness(0, 0, 0, 8)
        };
        W.Automation.AutomationProperties.SetName(button, "添加" + item.Title);
        button.Click += (_, _) => click();
        return button;
    }

    private static Fluent.Button ToggleButton(string name, Fluent.SymbolRegular symbol, Action click)
    {
        var button = new Fluent.Button
        {
            Content = new Fluent.SymbolIcon { Symbol = symbol, FontSize = 17 }, Padding = new W.Thickness(5),
            Appearance = Fluent.ControlAppearance.Secondary, ToolTip = name,
            HorizontalAlignment = W.HorizontalAlignment.Stretch, VerticalAlignment = W.VerticalAlignment.Stretch
        };
        W.Automation.AutomationProperties.SetName(button, name);
        button.Click += (_, _) => click();
        return button;
    }

    private static Media.Brush Brush(uint argb)
    {
        var value = new Media.SolidColorBrush(Media.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16),
            (byte)(argb >> 8), (byte)argb));
        value.Freeze();
        return value;
    }
}
