using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using Discoscope.Core.Models;
using Discoscope.Desktop.ViewModels;

namespace Discoscope.Desktop.Controls;

public sealed class TreemapView : FrameworkElement
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(nameof(Items),
        typeof(IReadOnlyList<TreemapItem>), typeof(TreemapView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnItemsChanged));

    private static void OnItemsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var map = (TreemapView)sender;
        map._selected = -1;
        map.Focusable = map.IsKeyboardFocused || map.Items?.Any(item => item.Directory is not null) == true;
        if (map.IsKeyboardFocused) map.SelectDirectory(1);
        map.UpdateKeyboardHelp();
    }

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
    public static Brush DirectoryBrush { get; } = FrozenBrush(28, 99, 135);
    public static Brush FileBrush { get; } = FrozenBrush(109, 67, 138);
    public static Brush OtherBrush => GroupBrush;
    private static readonly Pen FocusPen = new(Brushes.White, 2);
    private static readonly Pen Separator = new(BackgroundBrush, 0.5);
    private static readonly Typeface LabelTypeface = new("Segoe UI");
    private IReadOnlyList<TreemapTile> _tiles = [];
    private TreemapTile? _hovered;
    private long _total;
    private int _selected = -1;

    protected override void OnIsKeyboardFocusedChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnIsKeyboardFocusedChanged(e);
        if (IsKeyboardFocused && _selected < 0) SelectDirectory(1);
        if (!IsKeyboardFocused) Focusable = Items?.Any(item => item.Directory is not null) == true;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Left or Key.Up or Key.Right or Key.Down)
        {
            SelectDirectory(e.Key is Key.Left or Key.Up ? -1 : 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Items is { } items && _selected >= 0 &&
                 items[_selected].Directory is { } directory && NavigateCommand?.CanExecute(directory) == true)
        {
            NavigateCommand.Execute(directory);
            e.Handled = true;
        }
    }

    private void SelectDirectory(int step)
    {
        if (Items is not { Count: > 0 } items) return;
        var start = _selected < 0 && step < 0 ? 0 : _selected;
        for (int offset = 1; offset <= items.Count; offset++)
        {
            var index = (start + items.Count + offset * step) % items.Count;
            if (items[index].Directory is null) continue;
            _selected = index;
            UpdateKeyboardHelp();
            InvalidateVisual();
            return;
        }
    }

    private void UpdateKeyboardHelp()
    {
        var name = _selected >= 0 ? Items?[_selected].Name : null;
        var previous = AutomationProperties.GetHelpText(this);
        var help = name is null
            ? "Flèches pour choisir un dossier, Entrée pour l’ouvrir."
            : $"{name}. Flèches pour changer de dossier, Entrée pour l’ouvrir.";
        AutomationProperties.SetHelpText(this, help);
        if (previous != help && UIElementAutomationPeer.FromElement(this) is { } peer)
            peer.RaisePropertyChangedEvent(AutomationElementIdentifiers.HelpTextProperty, previous, help);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new TreemapAutomationPeer(this);

    private sealed class TreemapAutomationPeer(TreemapView owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(TreemapView);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
    }

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
            var brush = item.IsGroup ? GroupBrush : item.Directory is null ? FileBrush : DirectoryBrush;
            var bounds = new Rect(tile.X, tile.Y, tile.Width, tile.Height);
            dc.DrawRectangle(brush, Separator, bounds);
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
        if (IsKeyboardFocused && _selected >= 0 && _selected < _tiles.Count)
        {
            var tile = _tiles[_selected];
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));
            dc.DrawRectangle(null, FocusPen, new Rect(tile.X, tile.Y, tile.Width, tile.Height));
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
