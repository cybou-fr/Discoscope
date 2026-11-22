using System.ComponentModel;
using Discoscope.Core.Services;
using Discoscope.Desktop.ViewModels;
using Xunit;

namespace Discoscope.Desktop.Tests;

public sealed class ExplorerFilterTests
{
    [Fact]
    public async Task SortsAllVolumeMatchesBeforeApplyingTheDisplayLimit()
    {
        var root = Directory.CreateTempSubdirectory("DiscoscopeExplorerLimit-");
        try
        {
            for (int index = 0; index < ExplorerRows.VolumeSearchLimit; index++)
                File.WriteAllText(Path.Combine(root.FullName, $"match-{index:D5}.dat"), "x");
            var nested = root.CreateSubdirectory("nested");
            string largest = Path.Combine(nested.FullName, "match-largest.dat");
            File.WriteAllBytes(largest, new byte[128]);

            var scan = await new DiskScanner().ScanAsync(root.FullName, null, default);
            var results = new ExplorerRows(scan.Hierarchy!)
                .Filter("match-", ".dat", 0, null, default, wholeVolume: true);

            Assert.Equal(ExplorerRows.VolumeSearchLimit + 1, results.TotalMatches);
            Assert.DoesNotContain(results.Cast<ExplorerRow>(), row => row.FullPath == largest);

            var descending = results.Sort(nameof(ExplorerRow.Bytes), ListSortDirection.Descending);
            Assert.Equal(largest, Assert.IsType<ExplorerRow>(descending[0]).FullPath);
            Assert.Equal(ExplorerRows.VolumeSearchLimit, descending.Count);
            var ascending = descending.Sort(nameof(ExplorerRow.Bytes), ListSortDirection.Ascending);
            Assert.DoesNotContain(ascending.Cast<ExplorerRow>(), row => row.FullPath == largest);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task FiltersAndSortsUsingTheScanSnapshot()
    {
        var root = Directory.CreateTempSubdirectory("DiscoscopeExplorer-");
        var oldDate = DateTime.UtcNow.AddDays(-60);
        try
        {
            foreach (var (name, size, date) in new[]
            {
                ("annual-report.PDF", 32, oldDate),
                ("draft-report.pdf", 64, oldDate),
                ("new-report.pdf", 96, DateTime.UtcNow),
                ("small-report.pdf", 1, oldDate),
                ("old-report.txt", 96, oldDate)
            })
            {
                string path = Path.Combine(root.FullName, name);
                File.WriteAllBytes(path, new byte[size]);
                File.SetLastWriteTimeUtc(path, date);
            }

            var nested = root.CreateSubdirectory("nested");
            string nestedFile = Path.Combine(nested.FullName, "nested-report.pdf");
            File.WriteAllBytes(nestedFile, new byte[128]);
            File.SetLastWriteTimeUtc(nestedFile, oldDate);

            var scan = await new DiskScanner().ScanAsync(root.FullName, null, default);
            var hierarchy = Assert.IsType<Discoscope.Core.Models.ScanDirectory>(scan.Hierarchy);
            var rows = new ExplorerRows(hierarchy);
            var filtered = rows.Filter("REPORT", ".pdf", 16, oldDate.AddDays(1), default);
            var sorted = filtered.Sort(nameof(ExplorerRow.Bytes), ListSortDirection.Descending);

            Assert.Equal(new[] { "draft-report.pdf", "annual-report.PDF" },
                sorted.Cast<ExplorerRow>().Select(row => row.Name));
            Assert.Equal(6, filtered.Filter("", "", 0, null, default).Count);

            var volume = new ExplorerRows(Assert.Single(hierarchy.Directories))
                .Filter("REPORT", ".pdf", 16, oldDate.AddDays(1), default, wholeVolume: true)
                .Sort(nameof(ExplorerRow.Bytes), ListSortDirection.Descending);
            Assert.Equal(3, volume.TotalMatches);
            Assert.Equal(nestedFile, Assert.IsType<ExplorerRow>(volume[0]).FullPath);
            Assert.Equal(Path.Combine(root.FullName, "draft-report.pdf"),
                Assert.IsType<ExplorerRow>(volume[1]).FullPath);

            var reference = scan.CompletedAt.UtcDateTime;
            var intermediate = new ExplorerRows(hierarchy)
                .Filter("", "", 0, null, default, wholeVolume: true, ageBand: FileAgeBand.Intermediate,
                    ageReferenceUtc: reference);
            var ageTotal = FileTypeDistribution.AnalyzeDistributions(hierarchy, reference).Ages
                .Single(group => group.AgeBand == FileAgeBand.Intermediate);
            Assert.Equal(ageTotal.Files, intermediate.TotalMatches);
            Assert.Equal(ageTotal.LogicalBytes, intermediate.Cast<ExplorerRow>().Sum(row => row.Bytes));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
