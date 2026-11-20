using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Discoscope.Core.Models;
using Discoscope.Desktop.ViewModels;

namespace Discoscope.Desktop.Controls;

public sealed class TreemapView : FrameworkElement
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(nameof(Items),
        typeof(IReadOnlyList<TreemapItem>), typeof(TreemapView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DirectoryPathProperty = DependencyProperty.Register(nameof(DirectoryPath),
        typeof(string), typeof(TreemapView));

    public static readonly DependencyProperty NavigateCommandProperty = DependencyProperty.Register(nameof(NavigateCommand),
        typeof(ICommand), typeof(TreemapView));

    public IReadOnlyList<TreemapItem>? Items
    {
        get => (IReadOnlyList<TreemapItem>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public string? DirectoryPath
    {
        get => (string?)GetValue(DirectoryPathProperty);
        set => SetValue(DirectoryPathProperty, value);
    }

    public ICommand? NavigateCommand
    {
        get => (ICommand?)GetValue(NavigateCommandProperty);
        set => SetValue(NavigateCommandProperty, value);
    }

    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");
    private static readonly Brush BackgroundBrush = FrozenBrush(18, 26, 38);
    private static readonly Brush GroupBrush = FrozenBrush(61, 73, 94);
    private static readonly Brush[] DirectoryBrushes = [FrozenBrush(28, 99, 135), FrozenBrush(30, 108, 120), FrozenBrush(56, 86, 146)];
    private static readonly Brush[] FileBrushes = [FrozenBrush(109, 67, 138), FrozenBrush(124, 62, 113)];
    private static readonly Typeface LabelTypeface = new("Segoe UI");
    private IReadOnlyList<TreemapTile> _tiles = [];
    private TreemapTile? _hovered;
    private long _total;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var tile = FindTile(e.GetPosition(this));
        if (tile == _hovered) return;

        _hovered = tile;
        Cursor = tile?.Item.Directory is null ? Cursors.Arrow : Cursors.Hand;
        if (tile is not { Item: var item })
        {
            ToolTip = null;
            return;
        }

        var location = item.IsGroup ? DirectoryPath : item.Directory?.Path ?? Path.Join(DirectoryPath, item.Name);
        ToolTip = string.Create(French, $"{item.Name}\n{location}\n{SizeFormatter.Format(item.Bytes)} · {(double)item.Bytes / _total:P2}");
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = null;
        ToolTip = null;
        Cursor = Cursors.Arrow;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (FindTile(e.GetPosition(this))?.Item.Directory is { } directory && NavigateCommand?.CanExecute(directory) == true)
        {
            NavigateCommand.Execute(directory);
            e.Handled = true;
        }
    }

    private TreemapTile? FindTile(Point point)
    {
        foreach (var tile in _tiles)
            if (tile.Contains(point.X, point.Y)) return tile;
        return null;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        _hovered = null;
        ToolTip = null;
        _tiles = TreemapLayout.Arrange(Items ?? [], ActualWidth, ActualHeight);
        _total = _tiles.Sum(tile => tile.Item.Bytes);
        dc.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));
        for (int i = 0; i < _tiles.Count; i++)
        {
            var tile = _tiles[i];
            var item = tile.Item;
            var brush = item.IsGroup ? GroupBrush : item.Directory is null
                ? FileBrushes[i % FileBrushes.Length] : DirectoryBrushes[i % DirectoryBrushes.Length];
            var bounds = new Rect(tile.X, tile.Y, tile.Width, tile.Height);
            dc.DrawRectangle(brush, null, bounds);
            if (tile.Width < 42 || tile.Height < 22) continue;

            var text = new FormattedText(item.Name, French, FlowDirection.LeftToRight, LabelTypeface, 12,
                Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip)
            {
                MaxTextWidth = tile.Width - 10,
                MaxTextHeight = tile.Height - 7,
                Trimming = TextTrimming.CharacterEllipsis
            };
            dc.PushClip(new RectangleGeometry(bounds));
            dc.DrawText(text, new Point(tile.X + 5, tile.Y + 3));
            dc.Pop();
        }
    }

    private static Brush FrozenBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
