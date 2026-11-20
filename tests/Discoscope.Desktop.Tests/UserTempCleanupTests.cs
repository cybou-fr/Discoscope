using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Discoscope.Desktop.Services;
using Xunit;

namespace Discoscope.Desktop.Tests;

public sealed class UserTempCleanupTests : IDisposable
{
    private readonly DirectoryInfo _fixture = Directory.CreateTempSubdirectory("DiscoscopeCleanupTest-");
    private readonly DirectoryInfo _root;
    private readonly DirectoryInfo _inventoryFiles;
    private readonly DirectoryInfo _recycled;

    public UserTempCleanupTests()
    {
        _root = _fixture.CreateSubdirectory("source");
        _inventoryFiles = _fixture.CreateSubdirectory("inventory");
        _recycled = _fixture.CreateSubdirectory("recycled");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(2005)]
    public void InventoryAndCleanup_CoverEveryPage(int fileCount)
    {
        for (int i = 0; i < fileCount; i++) WriteOld($"fixture-{i:D4}.bin", i % 5);
        long bytes = Enumerable.Range(0, fileCount).Sum(i => (long)(i % 5));
        using var inventory = Index();
        Assert.Equal(fileCount, inventory.Count);
        Assert.Equal(bytes, inventory.LogicalBytes);
        Assert.Equal((fileCount + 999) / 1000, inventory.PageCount);
        Assert.Empty(inventory.ReadPage(-1));
        Assert.Empty(inventory.ReadPage(inventory.PageCount));
        Assert.Equal(fileCount, inventory.Pages().SelectMany(page => page).Select(file => file.Path).Distinct().Count());
        for (int page = 0; page < inventory.PageCount; page++)
            Assert.Equal(Math.Min(fileCount - page * 1000, 1000), inventory.ReadPage(page).Count);

        var updates = new List<UserTempCleanup.RecycleProgress>();
        var report = Recycle(inventory, progress: new Observer<UserTempCleanup.RecycleProgress>(updates.Add));

        Assert.Equal(new UserTempCleanup.Result(fileCount, 0, bytes), report.Result);
        Assert.False(report.Cancelled);
        Assert.Null(report.Failure);
        Assert.Empty(report.Errors);
        Assert.Empty(_root.EnumerateFiles());
        Assert.Equal(fileCount, _recycled.EnumerateFiles().Count());
        Assert.Equal(fileCount, updates[^1].Processed);
        Assert.Equal(bytes, updates[^1].LogicalBytes);
        Assert.Equal(updates.Select(p => p.Processed).Distinct().Order(), updates.Select(p => p.Processed));
        Assert.All(updates, p =>
        {
            Assert.Equal(fileCount, p.Total);
            Assert.Equal(p.Processed, p.Recycled + p.Skipped);
        });

        inventory.Dispose();
        Assert.Empty(_inventoryFiles.EnumerateFiles());
        Assert.Throws<ObjectDisposedException>(() => inventory.ReadPage(0));
    }

    [Fact]
    public void RecentFilesAreExcludedAndMissingOrChangedFilesAreSkipped()
    {
        var missing = WriteOld("missing.txt", 2);
        var modified = WriteOld("modified.txt", 2);
        var timestampOnly = WriteOld("timestamp.txt", 2);
        var retained = WriteOld("retained.txt", 5);
        var recent = Path.Join(_root.FullName, "recent.txt");
        File.WriteAllText(recent, "recent");
        using var inventory = Index();
        Assert.Equal(4, inventory.Count);
        File.Delete(missing);
        File.WriteAllText(modified, "changed");
        File.SetLastWriteTimeUtc(timestampOnly, DateTime.UtcNow.AddDays(-8));

        var report = Recycle(inventory);

        Assert.Equal(new UserTempCleanup.Result(1, 3, 5), report.Result);
        Assert.Equal(3, report.Errors.Count);
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(modified));
        Assert.True(File.Exists(timestampOnly));
        Assert.False(File.Exists(retained));
    }

    [Fact]
    public void CleanupSkipsAReplacementWithTheSameSizeAndTimestamp()
    {
        var original = WriteOld("target.bin", 3);
        var replacement = WriteOld("replacement.bin", 3);
        File.WriteAllBytes(replacement, [1, 2, 3]);
        File.SetLastWriteTimeUtc(replacement, File.GetLastWriteTimeUtc(original));
        using var inventory = Index();
        File.Move(original, Path.Join(_fixture.FullName, "original.bin"));
        File.Move(replacement, original);

        var report = Recycle(inventory);

        Assert.Equal(new UserTempCleanup.Result(0, 2, 0), report.Result);
        Assert.Contains(report.Errors, message => message.Contains("a été remplacé", StringComparison.Ordinal));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(original));
        Assert.Empty(_recycled.EnumerateFiles());
    }

    [Fact]
    public void LastPathCheckDetectsReplacementAfterOpeningTheValidatedHandle()
    {
        var original = WriteOld("target.bin", 3);
        var replacement = WriteOld("replacement.bin", 3);
        File.SetLastWriteTimeUtc(replacement, File.GetLastWriteTimeUtc(original));
        using var guard = CleanupFileGuard.Open(_root.FullName, original);
        File.Move(original, Path.Join(_root.FullName, "original.bin"));
        File.Move(replacement, original);

        Assert.Throws<IOException>(() => guard.VerifyPathStillMatches(original));
        Assert.True(File.Exists(original));
    }

    [Fact]
    public void MissingRootDoesNotLeaveAnInventory()
    {
        Assert.Throws<IOException>(() => UserTempCleanup.CreateInventory(
            Path.Join(_fixture.FullName, "missing"), _inventoryFiles.FullName));
        Assert.Empty(_inventoryFiles.EnumerateFiles());
    }

    [Fact]
    public void CancellingIndexingReleasesTheBackingFile()
    {
        WriteOld("old.txt", 1);
        using var cancellation = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => Index(cancellation.Token,
            new Observer<UserTempCleanup.Progress>(_ => cancellation.Cancel())));
        Assert.Empty(_inventoryFiles.EnumerateFiles());
        Assert.Single(_root.EnumerateFiles());
    }

    [Fact]
    public void CancellingDuringSecondPageReturnsOnlyCompletedWork()
    {
        for (int i = 0; i < 1015; i++) WriteOld($"{i:D4}.bin", 1);
        using var inventory = Index();
        using var cancellation = new CancellationTokenSource();
        int moved = 0;
        var updates = new List<UserTempCleanup.RecycleProgress>();
        var report = UserTempCleanup.RecycleInventory(inventory, _root.FullName, path =>
        {
            MoveFixtureFile(path);
            if (++moved == 1003) cancellation.Cancel();
        }, cancellation.Token, new Observer<UserTempCleanup.RecycleProgress>(updates.Add));

        Assert.True(report.Cancelled);
        Assert.Equal(new UserTempCleanup.Result(1003, 0, 1003), report.Result);
        Assert.Equal(1003, updates[^1].Processed);
        Assert.Equal(12, _root.EnumerateFiles().Count());
    }

    [Fact]
    public void LockedAndInaccessibleFilesAreReportedWithoutStoppingCleanup()
    {
        var locked = WriteOld("locked.bin", 5);
        var denied = WriteOld("denied.bin", 7);
        WriteOld("ok.bin", 11);
        using var inventory = Index();
        using var identity = WindowsIdentity.GetCurrent();
        var deniedInfo = new FileInfo(denied);
        var security = deniedInfo.GetAccessControl();
        var rule = new FileSystemAccessRule(identity.User!, FileSystemRights.ReadData, AccessControlType.Deny);
        security.AddAccessRule(rule);
        deniedInfo.SetAccessControl(security);
        try
        {
            using var exclusive = File.Open(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var report = Recycle(inventory);
            Assert.Equal(new UserTempCleanup.Result(1, 2, 11), report.Result);
            Assert.Equal(2, report.Errors.Count);
            Assert.True(File.Exists(locked));
        }
        finally
        {
            security.RemoveAccessRuleSpecific(rule);
            deniedInfo.SetAccessControl(security);
        }
        Assert.True(File.Exists(denied));
    }

    [Fact]
    public void GuardPreventsRenamingTheContainingDirectory()
    {
        var file = WriteOld("guarded.bin", 2);
        var renamed = Path.Join(_fixture.FullName, "renamed");
        using (CleanupFileGuard.Open(_root.FullName, file))
        {
            var error = Record.Exception(() => Directory.Move(_root.FullName, renamed));
            Assert.True(error is IOException or UnauthorizedAccessException, $"Unexpected rename result: {error}");
            Assert.True(File.Exists(file));
        }
    }

    [Fact]
    public async Task JunctionsAndAlternateRootsAreRejected()
    {
        var outside = _fixture.CreateSubdirectory("outside");
        var protectedFile = Path.Join(outside.FullName, "protected.bin");
        File.WriteAllBytes(protectedFile, [1]);
        File.SetLastWriteTimeUtc(protectedFile, DateTime.UtcNow.AddDays(-10));
        var junction = Path.Join(_root.FullName, "alias");
        using var command = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Join(Environment.SystemDirectory, "cmd.exe"),
            Arguments = $"/d /c mklink /J \"{junction}\" \"{outside.FullName}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        await command.WaitForExitAsync();
        Assert.Equal(0, command.ExitCode);
        try
        {
            using var inventory = Index();
            Assert.Empty(inventory.ReadPage(0));
            Assert.Throws<IOException>(() => CleanupFileGuard.Open(_root.FullName, Path.Join(junction, "protected.bin")));
            Assert.Throws<IOException>(() => CleanupFileGuard.Open(_root.FullName, protectedFile));
            Assert.Throws<IOException>(() => CleanupFileGuard.Open(_root.FullName,
                Path.Join(_root.FullName, "..", "outside", "protected.bin")));
            Assert.Throws<InvalidOperationException>(() =>
                UserTempCleanup.RecycleInventory(inventory, outside.FullName, MoveFixtureFile));
            Assert.True(File.Exists(protectedFile));
        }
        finally
        {
            Directory.Delete(junction);
        }
    }

    private UserTempCleanup.Inventory Index(CancellationToken token = default,
        IProgress<UserTempCleanup.Progress>? progress = null) =>
        UserTempCleanup.CreateInventory(_root.FullName, _inventoryFiles.FullName, token, progress);

    private UserTempCleanup.RecycleReport Recycle(UserTempCleanup.Inventory inventory,
        IProgress<UserTempCleanup.RecycleProgress>? progress = null) =>
        UserTempCleanup.RecycleInventory(inventory, _root.FullName, MoveFixtureFile, progress: progress);

    private void MoveFixtureFile(string path)
    {
        Assert.Equal(_root.FullName, Path.GetDirectoryName(path));
        File.Move(path, Path.Join(_recycled.FullName, Path.GetFileName(path)));
    }

    private string WriteOld(string name, int size)
    {
        var path = Path.Join(_root.FullName, name);
        File.WriteAllBytes(path, new byte[size]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-10));
        return path;
    }

    public void Dispose() => _fixture.Delete(recursive: true);

    private sealed class Observer<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
