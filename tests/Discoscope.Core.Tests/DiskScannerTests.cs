using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Discoscope.Core.Models;
using Discoscope.Core.Services;

namespace Discoscope.Core.Tests;

public sealed class DiskScannerTests
{
    [Fact]
    public async Task Scan_SumsNestedDirectoriesAndFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "DiscoscopeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "one", "two"));
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(root, "one", "first.bin"), new byte[128]);
            await File.WriteAllBytesAsync(Path.Combine(root, "one", "two", "second.bin"), new byte[64]);

            var progress = new ScanObserver();
            var result = await new DiskScanner().ScanAsync(root, progress, CancellationToken.None);

            Assert.Equal(192, result.TotalBytes);
            Assert.Equal(0, result.RootDirectBytes);
            Assert.Equal(0, result.RootDirectFileCount);
            Assert.Contains(result.Entries, e => e.Path == Path.Combine(root, "one") && e.DirectBytes == 128 && e.DirectFileCount == 1);
            Assert.Contains(result.Entries, e => e.Path == Path.Combine(root, "one", "two") && e.DirectBytes == 64 && e.DirectFileCount == 1);
            Assert.Equal(2, result.FileCount);
            Assert.Equal(3, result.DirectoryCount);
            Assert.Contains(result.Entries, x => x.Path == Path.Combine(root, "one")
                && x.Bytes == 192 && x.FileCount == 2 && x.DirectoryCount == 1);
            Assert.Contains(result.Entries, x => x.Path == Path.Combine(root, "one", "two") && x.Bytes == 64);
            Assert.Equal(new ScanProgress(2, 3, 192, root), progress.Last);
            Assert.Equal(128, new FileInfo(Path.Combine(root, "one", "first.bin")).Length);
            var hierarchy = Assert.IsType<ScanDirectory>(result.Hierarchy);
            var one = Assert.Single(hierarchy.Directories);
            Assert.Same(hierarchy, one.Parent);
            Assert.Equal(192, one.Bytes);
            Assert.Equal(128, one.DirectBytes);
            Assert.Equal(1, one.DirectFileCount);
            Assert.Equal(new ScanFile("first.bin", 128), Assert.Single(one.Files));
            var two = Assert.Single(one.Directories);
            Assert.Equal(Path.Combine(root, "one", "two", "second.bin"),
                Path.Combine(two.Path, Assert.Single(two.Files).Name));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Scan_RejectsMissingRoot()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            new DiskScanner().ScanAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), null, CancellationToken.None));
    }

    [Fact]
    public async Task Scan_RetainsOnlyTheLargest200FilesIncludingEmptyFiles()
    {
        var root = Directory.CreateTempSubdirectory("DiscoscopeTests-");
        try
        {
            for (int i = 0; i < 204; i++)
                File.WriteAllBytes(Path.Combine(root.FullName, $"{i:D3}.bin"), new byte[Math.Max(0, i - 10)]);

            var largePath = Path.Combine(root.FullName, "large.dat");
            const long largeSize = (long)int.MaxValue + 1;
            using (var stream = File.Create(largePath))
                stream.SetLength(largeSize);

            var result = await new DiskScanner().ScanAsync(root.FullName, null, CancellationToken.None);

            Assert.Equal(205, result.FileCount);
            Assert.Equal(200, result.LargestFiles.Count);
            Assert.Equal(new LargeFileEntry(largePath, largeSize), result.LargestFiles[0]);
            Assert.Equal(Enumerable.Range(1, 193).Reverse().Select(i => (long)i).Concat(new long[6]),
                result.LargestFiles.Skip(1).Select(file => file.Bytes));
            Assert.All(result.LargestFiles.Skip(1), file => Assert.Equal(".bin", Path.GetExtension(file.Path)));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Scan_CanBeCancelledByProgressObserver()
    {
        var root = Directory.CreateTempSubdirectory("DiscoscopeTests-");
        using var cancellation = new CancellationTokenSource();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new DiskScanner().ScanAsync(root.FullName, new ScanObserver(cancellation.Cancel), cancellation.Token));
        }
        finally
        {
            root.Delete();
        }
    }

    [Fact]
    public async Task Scan_ReportsInaccessibleDirectoryAndContinuesOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;

        var root = Directory.CreateTempSubdirectory("DiscoscopeTests-");
        var blocked = root.CreateSubdirectory("blocked");
        using var identity = WindowsIdentity.GetCurrent();
        var restricted = blocked.GetAccessControl();
        var rule = new FileSystemAccessRule(identity.User!,
            FileSystemRights.ListDirectory, AccessControlType.Deny);
        try
        {
            File.WriteAllText(Path.Combine(blocked.FullName, "hidden.txt"), "hidden");
            File.WriteAllText(Path.Combine(root.FullName, "visible.txt"), "visible");
            restricted.AddAccessRule(rule);
            blocked.SetAccessControl(restricted);

            var result = await new DiskScanner().ScanAsync(root.FullName, null, CancellationToken.None);

            Assert.Equal(1, result.FileCount);
            Assert.Equal(7, result.TotalBytes);
            Assert.Contains(result.Issues, issue => issue.Path == blocked.FullName);
            Assert.Empty(Assert.Single(result.Hierarchy!.Directories).Files);
            var locations = await TemporaryFilesInspector.InspectLocationsAsync(
                [("Blocked", blocked.FullName)], result.Hierarchy, CancellationToken.None);
            var location = Assert.Single(locations);
            Assert.False(location.Accessible);
            Assert.Equal(1, location.SkippedCount);
        }
        finally
        {
            restricted.RemoveAccessRuleSpecific(rule);
            blocked.SetAccessControl(restricted);
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Scan_IgnoresJunctionBackToRootOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;

        var root = Directory.CreateTempSubdirectory("DiscoscopeTests-");
        var junction = Path.Combine(root.FullName, "loop");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "file.txt"), "data");
            using var command = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = $"/d /c mklink /J \"{junction}\" \"{root.FullName}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            })!;
            await command.WaitForExitAsync();
            Assert.Equal(0, command.ExitCode);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var scanner = new DiskScanner();
            var result = await scanner.ScanAsync(root.FullName, null, timeout.Token);
            Assert.Equal(1, result.FileCount);
            Assert.Equal(1, result.DirectoryCount);
            Assert.Equal(junction, Assert.Single(result.Issues).Path);
            await Assert.ThrowsAsync<IOException>(() => scanner.ScanAsync(junction, null, timeout.Token));
        }
        finally
        {
            if (Directory.Exists(junction)) Directory.Delete(junction);
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Scan_LimitedOverviewMatchesLegacyOrderingAndCanBeCancelled()
    {
        var root = Directory.CreateTempSubdirectory("DiscoscopeTests-");
        try
        {
            foreach (var (name, size) in new[] { ("one", 4), ("two", 4), ("three", 8), ("empty", 0) })
            {
                var child = root.CreateSubdirectory(name);
                File.WriteAllBytes(Path.Combine(child.FullName, "file.bin"), new byte[size]);
            }

            var result = await new DiskScanner().ScanAsync(root.FullName, null, CancellationToken.None);
            Assert.Equal(4, result.Entries.Count);
            Assert.Equal(4, result.Hierarchy!.Directories.Count);
            Assert.Equal(result.Entries.Take(2), result.GetLargestDirectories(2));
            Assert.Equal(result.Entries, result.GetLargestDirectories(10));
            Assert.Equal(0, Assert.Single(result.Hierarchy.Directories.Single(d => d.Name == "empty").Files).Bytes);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => result.GetLargestDirectories(2, cancellation.Token));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task TemporaryInventory_ReusesCoveredSnapshotAndScansUncoveredLocations()
    {
        var root = Directory.CreateTempSubdirectory("DiscoscopeTests-");
        var outside = Directory.CreateTempSubdirectory("DiscoscopeTests-");
        try
        {
            var temporary = root.CreateSubdirectory("TEMP");
            File.WriteAllText(Path.Combine(temporary.FullName, "before.txt"), "abc");
            File.WriteAllText(Path.Combine(outside.FullName, "outside.txt"), "12345");
            var scan = await new DiskScanner().ScanAsync(root.FullName, null, CancellationToken.None);
            File.WriteAllText(Path.Combine(temporary.FullName, "later.txt"), "later");
            var results = await TemporaryFilesInspector.InspectLocationsAsync(
                [("Covered", temporary.FullName + Path.DirectorySeparatorChar), ("Uncovered", outside.FullName)],
                scan.Hierarchy, CancellationToken.None);

            Assert.Collection(results,
                covered => { Assert.Equal(3, covered.LogicalBytes); Assert.Equal(1, covered.FileCount); },
                uncovered => { Assert.Equal(5, uncovered.LogicalBytes); Assert.Equal(1, uncovered.FileCount); });
        }
        finally
        {
            root.Delete(recursive: true);
            outside.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Scan_AggregatesDeepHierarchyAndKeepsRootFilesSeparate()
    {
        var root = Directory.CreateTempSubdirectory("DiscoscopeTests-");
        try
        {
            var deepest = root;
            for (int i = 0; i < 64; i++)
                deepest = deepest.CreateSubdirectory("d");
            File.WriteAllText(Path.Combine(root.FullName, "root.txt"), "abc");
            File.WriteAllText(Path.Combine(deepest.FullName, "leaf.txt"), "12345");

            var result = await new DiskScanner().ScanAsync(root.FullName, null, CancellationToken.None);
            Assert.Equal(65, result.DirectoryCount);
            Assert.Equal(8, result.TotalBytes);
            var directory = Assert.IsType<ScanDirectory>(result.Hierarchy);
            Assert.Equal(3, directory.DirectBytes);
            Assert.Equal(new ScanFile("root.txt", 3), Assert.Single(directory.Files));
            for (int i = 0; i < 64; i++)
            {
                directory = Assert.Single(directory.Directories);
                Assert.Equal(5, directory.Bytes);
            }
            Assert.Equal(new ScanFile("leaf.txt", 5), Assert.Single(directory.Files));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private sealed class ScanObserver(Action? action = null) : IProgress<ScanProgress>
    {
        public ScanProgress? Last { get; private set; }

        public void Report(ScanProgress value)
        {
            Last = value;
            action?.Invoke();
        }
    }
}
