using Discoscope.Core.Models;

namespace Discoscope.Core.Tests;

public sealed class TreemapLayoutTests
{
    [Fact]
    public void Summarize_AccountsForDirectFilesAndNestedDirectoriesExactlyOnce()
    {
        var root = new ScanDirectory("root", null) { Bytes = 1000 };
        var child = new ScanDirectory("child", root)
        {
            Bytes = 600,
            Files = [new("nested.bin", 600)]
        };
        root.Directories = [child];
        root.Files = [new("direct.bin", 397), new("tiny.bin", 3), new("empty.bin", 0)];

        var items = TreemapLayout.Summarize(root);
        Assert.Collection(items,
            item => Assert.Same(child, item.Directory),
            item => Assert.Equal(new TreemapItem("direct.bin", 397), item),
            item => Assert.Equal(new TreemapItem("Autres", 3, IsGroup: true), item));
        Assert.Equal(root.Bytes, items.Sum(item => item.Bytes));
        Assert.Equal(600, Assert.Single(TreemapLayout.Summarize(child)).Bytes);
    }

    [Theory]
    [InlineData(800, 300)]
    [InlineData(200, 700)]
    public void Arrange_PreservesAreasAndHasUniqueHitTargets(double width, double height)
    {
        TreemapItem[] items = [new("large", 501), new("medium", 300), new("small", 199)];
        var tiles = TreemapLayout.Arrange(items, width, height);
        Assert.Equal(width * height, tiles.Sum(tile => tile.Width * tile.Height), 7);
        foreach (var tile in tiles)
        {
            Assert.Equal(tile.Item.Bytes / 1000.0, tile.Width * tile.Height / (width * height), 10);
            Assert.Equal(tile, Assert.Single(tiles, other =>
                other.Contains(tile.X + tile.Width / 2, tile.Y + tile.Height / 2)));
        }
    }

    [Fact]
    public void Summarize_BoundsTileCountAndHandlesZeroBytes()
    {
        var directory = new ScanDirectory("root", null) { Files = [new("empty", 0)] };
        Assert.Empty(TreemapLayout.Arrange(TreemapLayout.Summarize(directory), 800, 400));
        directory.Files = Enumerable.Range(0, 100).Select(i => new ScanFile(i.ToString(), 1)).ToArray();
        directory.Bytes = 100;
        var items = TreemapLayout.Summarize(directory);
        Assert.Equal(64, items.Count);
        Assert.Equal(100, items.Sum(item => item.Bytes));
        Assert.Equal(37, Assert.Single(items, item => item.IsGroup).Bytes);
    }
}
