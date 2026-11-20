using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Discoscope.Desktop.ViewModels;

namespace Discoscope.Desktop.Controls;

/// <summary>Read-only proportional view of the largest direct child directories.</summary>
public sealed class TreemapView : FrameworkElement
{
    public static readonly DependencyProperty ItemsProperty =
        DependencyProperty.Register(nameof(Items), typeof(IEnumerable<DirectoryRow>),
            typeof(TreemapView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable<DirectoryRow>? Items
    {
        get => (IEnumerable<DirectoryRow>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(18, 26, 38)), null, new Rect(0, 0, width, height));
        var items = Items?.Where(x => x.Bytes > 0).OrderByDescending(x => x.Bytes).Take(24).ToArray() ?? [];
        if (items.Length == 0) return;

        // Slice-and-dice: exact proportional areas, bounded number of rendered rectangles.
        double total = items.Sum(x => (double)x.Bytes);
        var bounds = new Rect(0, 0, width, height);
        for (int i = 0; i < items.Length; i++)
        {
            var item = items[i];
            double share = item.Bytes / total;
            var rect = bounds.Width >= bounds.Height
                ? new Rect(bounds.X, bounds.Y, bounds.Width * share, bounds.Height)
                : new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height * share);
            bounds = bounds.Width >= bounds.Height
                ? new Rect(bounds.X + rect.Width, bounds.Y, bounds.Width - rect.Width, bounds.Height)
                : new Rect(bounds.X, bounds.Y + rect.Height, bounds.Width, bounds.Height - rect.Height);
            // Recompute relative shares for the remaining unallocated area.
            total -= item.Bytes;
            if (rect.Width < 2 || rect.Height < 2) continue;
            var color = Color.FromRgb((byte)(33 + i * 5 % 35), (byte)(105 + i * 11 % 65), (byte)(150 + i * 7 % 85));
            var fill = new SolidColorBrush(color);
            dc.DrawRoundedRectangle(fill, new Pen(new SolidColorBrush(Color.FromRgb(18, 26, 38)), 2),
                new Rect(rect.X + 1, rect.Y + 1, Math.Max(0, rect.Width - 2), Math.Max(0, rect.Height - 2)), 5, 5);
            if (rect.Width < 85 || rect.Height < 32) continue;
            var label = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(item.Path));
            if (string.IsNullOrEmpty(label)) label = item.Path;
            var formatted = new FormattedText(label + " · " + item.SizeLabel,
                System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 12, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip)
            { MaxTextWidth = Math.Max(1, rect.Width - 16), MaxTextHeight = Math.Max(1, rect.Height - 12),
              Trimming = TextTrimming.CharacterEllipsis };
            dc.DrawText(formatted, new Point(rect.X + 8, rect.Y + 6));
        }
    }
}
