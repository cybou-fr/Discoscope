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
    public void AnalyzeDistributions_CountsEachFileOnceAcrossAgeBoundaries()
    {
        var reference = new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
        var root = new ScanDirectory("root", null)
        {
            Files =
            [
                new("recent.txt", 1) { ModifiedUtc = reference.AddDays(-30) },
                new("unknown.txt", 2)
            ]
        };
        root.Directories = [new ScanDirectory("child", root)
        {
            Files =
            [
                new("mid.txt", 4) { ModifiedUtc = reference.AddDays(-365) },
                new("old.txt", 8) { ModifiedUtc = reference.AddDays(-365).AddTicks(-1) }
            ]
        }];

        var totals = FileTypeDistribution.AnalyzeDistributions(root, reference);

        Assert.Equal(new long[] { 1, 4, 8, 2 }, totals.Ages.Select(age => age.LogicalBytes));
        Assert.All(totals.Ages, age => Assert.Equal(1, age.Files));
        Assert.Equal(Assert.Single(totals.Types).LogicalBytes, totals.Ages.Sum(age => age.LogicalBytes));
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
