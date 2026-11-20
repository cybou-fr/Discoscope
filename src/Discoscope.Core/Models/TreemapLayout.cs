namespace Discoscope.Core.Models;

public readonly record struct TreemapItem(string Name, long Bytes, ScanDirectory? Directory = null, bool IsGroup = false);
public readonly record struct TreemapTile(TreemapItem Item, double X, double Y, double Width, double Height)
{
    public bool Contains(double x, double y) => x >= X && x < X + Width && y >= Y && y < Y + Height;
}

public static class TreemapLayout
{
    // Limit visual complexity while accounting for every byte in a single remainder tile.
    public static IReadOnlyList<TreemapItem> Summarize(ScanDirectory directory, CancellationToken token = default)
    {
        const int itemLimit = 63;
        var retained = new PriorityQueue<TreemapItem, long>();
        long groupedBytes = 0;
        // Items smaller than 0.5% of this directory are grouped.
        long minimumBytes = Math.Max(1, directory.Bytes / 200);
        foreach (var child in directory.Directories)
            Add(new TreemapItem(child.Name, child.Bytes, child));
        foreach (var file in directory.Files)
            Add(new TreemapItem(file.Name, file.Bytes));

        var items = retained.UnorderedItems.Select(entry => entry.Element).ToList();
        if (groupedBytes > 0)
            items.Add(new TreemapItem("Autres", groupedBytes, IsGroup: true));
        return items.OrderByDescending(item => item.Bytes).ToArray();

        void Add(TreemapItem item)
        {
            token.ThrowIfCancellationRequested();
            if (item.Bytes < minimumBytes)
                groupedBytes += item.Bytes;
            else if (retained.Count < itemLimit)
                retained.Enqueue(item, item.Bytes);
            else
                groupedBytes += retained.EnqueueDequeue(item, item.Bytes).Bytes;
        }
    }

    public static IReadOnlyList<TreemapTile> Arrange(IReadOnlyList<TreemapItem> items, double width, double height)
    {
        if (items.Count == 0 || width <= 0 || height <= 0)
            return [];

        var tiles = new TreemapTile[items.Count];
        Split(0, items.Count, items.Sum(item => item.Bytes), 0, 0, width, height);
        return tiles;

        void Split(int start, int end, long total, double x, double y, double w, double h)
        {
            if (end - start == 1)
            {
                tiles[start] = new TreemapTile(items[start], x, y, w, h);
                return;
            }

            long left = items[start].Bytes;
            int middle = start + 1;
            while (middle < end - 1 && left < total / 2)
                left += items[middle++].Bytes;
            double fraction = left / (double)total;
            if (w >= h)
            {
                double split = w * fraction;
                Split(start, middle, left, x, y, split, h);
                Split(middle, end, total - left, x + split, y, w - split, h);
            }
            else
            {
                double split = h * fraction;
                Split(start, middle, left, x, y, w, split);
                Split(middle, end, total - left, x, y + split, w, h - split);
            }
        }
    }
}
