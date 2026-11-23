using W = System.Windows;
using Controls = System.Windows.Controls;
using Media = System.Windows.Media;
using Fluent = Wpf.Ui.Controls;
using LyricWallpaper.Widgets;

namespace LyricWallpaper.Presentation;

internal sealed class WidgetDock
{
    private readonly Controls.Grid expanded = new();
    private readonly Fluent.Button expand;
    private bool isExpanded = true;

    public Controls.Border View { get; }
    public W.Rect Bounds { get; private set; } = W.Rect.Empty;

    public WidgetDock(WidgetCatalog catalog, bool canAdd, Action<WidgetDefinition> add, Action changed)
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
            changed();
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

    private static Fluent.Button Card(WidgetDefinition item, bool enabled, Action click)
    {
        var row = new Controls.DockPanel { LastChildFill = true };
        var symbol = Enum.TryParse<Fluent.SymbolRegular>(item.Icon, out var icon) ? icon : Fluent.SymbolRegular.Add24;
        var tile = new Controls.Border
        {
            Width = 35, Height = 35, Margin = new W.Thickness(0, 0, 9, 0), CornerRadius = new W.CornerRadius(8),
            Background = Brush(0xFFE4F0FC),
            Child = new Fluent.SymbolIcon
            {
                Symbol = symbol, FontSize = 19, Foreground = Brush(0xFF0067C0),
                HorizontalAlignment = W.HorizontalAlignment.Center, VerticalAlignment = W.VerticalAlignment.Center
            }
        };
        Controls.DockPanel.SetDock(tile, Controls.Dock.Left);
        row.Children.Add(tile);
        var plus = new Fluent.SymbolIcon
        {
            Symbol = Fluent.SymbolRegular.Add16, FontSize = 16, VerticalAlignment = W.VerticalAlignment.Center,
            Foreground = Brush(0xFF526176)
        };
        Controls.DockPanel.SetDock(plus, Controls.Dock.Right);
        row.Children.Add(plus);
        var label = new Controls.StackPanel { VerticalAlignment = W.VerticalAlignment.Center };
        label.Children.Add(new Controls.TextBlock { Text = item.Title, FontWeight = W.FontWeights.SemiBold });
        label.Children.Add(new Controls.TextBlock { Text = item.Category, FontSize = 11, Foreground = Brush(0xFF596679) });
        row.Children.Add(label);
        var button = new Fluent.Button
        {
            Content = row, Padding = new W.Thickness(9, 8, 9, 8), MinHeight = 52, IsEnabled = enabled,
            HorizontalAlignment = W.HorizontalAlignment.Stretch, HorizontalContentAlignment = W.HorizontalAlignment.Stretch,
            Appearance = Fluent.ControlAppearance.Secondary, Margin = new W.Thickness(0, 0, 0, 6)
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
