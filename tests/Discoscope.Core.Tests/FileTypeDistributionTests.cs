using Discoscope.Core.Models;
using Discoscope.Core.Services;

namespace Discoscope.Core.Tests;

public sealed class FileTypeDistributionTests
{
    [Fact]
    public void Analyze_GroupsExtensionsAcrossTheSnapshot()
    {
        var root = new ScanDirectory("root", null)
        {
            Files = [new("image.JPEG", 8), new("README", 3), new("empty.bin", 0)]
        };
        root.Directories = [new ScanDirectory("child", root)
        {
            Files = [new("icon.png", 7), new("report.pdf", 20)]
        }];

        var types = FileTypeDistribution.Analyze(root);

        Assert.Equal(5, types.Sum(type => type.Files));
        Assert.Equal(38, types.Sum(type => type.LogicalBytes));
        Assert.Equal(new FileTypeTotal("Images", 2, 15), types.Single(type => type.Name == "Images"));
        Assert.Equal(new FileTypeTotal("Autres extensions", 1, 0), types.Single(type => type.Name == "Autres extensions"));
        Assert.Equal("Documents", types[0].Name);
    }

    [Fact]
    public void Analyze_ObservesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            FileTypeDistribution.Analyze(new ScanDirectory("root", null), cancellation.Token));
    }
}
