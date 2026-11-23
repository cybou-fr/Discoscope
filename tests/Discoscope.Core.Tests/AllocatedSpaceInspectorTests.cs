using System.Runtime.InteropServices;
using Discoscope.Core.Services;
using Microsoft.Win32.SafeHandles;

namespace Discoscope.Core.Tests;

public sealed class AllocatedSpaceInspectorTests
{
    [Fact]
    public async Task CountsHardLinksOnceAndReportsMissingSnapshotEntries()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("DiscoscopeAllocation-");
        try
        {
            var drive = new DriveInfo(root.Root.FullName);
            if (drive.DriveType != DriveType.Fixed || drive.DriveFormat != "NTFS") return;

            var first = root.CreateSubdirectory("first");
            var second = root.CreateSubdirectory("second");
            var nested = first.CreateSubdirectory("nested");
            string original = Path.Combine(nested.FullName, "original.bin");
            string link = Path.Combine(second.FullName, "link.bin");
            string siblingLink = Path.Combine(first.FullName, "sibling-link.bin");
            string missing = Path.Combine(second.FullName, "missing.bin");
            string changed = Path.Combine(second.FullName, "changed.bin");
            File.WriteAllBytes(original, new byte[8192]);
            File.WriteAllBytes(missing, new byte[8192]);
            File.WriteAllBytes(changed, new byte[8192]);
            var lastWrite = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(changed, lastWrite);
            Assert.True(CreateHardLink(link, original, IntPtr.Zero));
            Assert.True(CreateHardLink(siblingLink, original, IntPtr.Zero));

            var scan = await new DiskScanner().ScanAsync(root.FullName, null, default);
            File.Delete(missing);
            File.SetLastWriteTimeUtc(changed, lastWrite.AddHours(1));
            var inspector = new AllocatedSpaceInspector();
            var result = await inspector.InspectAsync(scan.Hierarchy!);
            Assert.Equal(1, result.MeasuredPaths);
            Assert.Equal(2, result.UnavailablePaths);
            Assert.Equal(2, result.DuplicateLinks);
            Assert.Equal(8192, result.MeasuredLogicalBytes);
            Assert.InRange(result.Bytes, 8192, 65536);
            var ranked = Assert.Single(result.LargestFiles);
            Assert.Equal(result.Bytes, ranked.AllocatedBytes);
            Assert.Equal(8192, ranked.LogicalBytes);
            Assert.Equal(2, result.LargestFolders.Count);
            Assert.All(result.LargestFolders, folder =>
            {
                Assert.Equal(result.Bytes, folder.AllocatedBytes);
                Assert.Equal(8192, folder.LogicalBytes);
                Assert.Equal(folder.Directory.Name == "second", folder.IsPartial);
            });

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                inspector.InspectAsync(scan.Hierarchy!, cancellation.Token));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RankingKeepsAtMostTwoHundredFilesOrderedByAllocatedBytes()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("DiscoscopeAllocatedRanking-");
        try
        {
            var drive = new DriveInfo(root.Root.FullName);
            if (drive.DriveType != DriveType.Fixed || drive.DriveFormat != "NTFS") return;

            for (int index = 0; index < 200; index++)
                File.WriteAllBytes(Path.Combine(root.FullName, $"empty-{index:D3}.bin"), []);
            string largest = Path.Combine(root.FullName, "largest.bin");
            File.WriteAllBytes(largest, new byte[256 * 1024]);

            var scan = await new DiskScanner().ScanAsync(root.FullName, null, default);
            var inspector = new AllocatedSpaceInspector();
            var updates = new List<AllocatedSpaceProgress>();
            var result = await inspector.InspectAsync(scan.Hierarchy!,
                new InlineProgress<AllocatedSpaceProgress>(updates.Add), default);

            Assert.Equal(201, result.MeasuredPaths);
            Assert.Equal(200, result.LargestFiles.Count);
            Assert.Equal(largest, result.LargestFiles[0].Path);
            Assert.All(result.LargestFiles.Skip(1), file => Assert.Equal(0, file.AllocatedBytes));
            Assert.Equal(new AllocatedSpaceProgress(201, 201, result.Bytes), updates[^1]);

            using var cancellation = new CancellationTokenSource();
            var update = new InlineProgress<AllocatedSpaceProgress>(value =>
            {
                Assert.InRange(value.ProcessedPaths, 1, 200);
                cancellation.Cancel();
            });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                inspector.InspectAsync(scan.Hierarchy!, update, cancellation.Token));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DistinguishesAllocatedAndLogicalBytesForSparseFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("DiscoscopeSparse-");
        try
        {
            var drive = new DriveInfo(root.Root.FullName);
            if (drive.DriveType != DriveType.Fixed || drive.DriveFormat != "NTFS") return;

            const uint setSparse = 0x000900C4;
            const long logicalSize = 64 * 1024 * 1024;
            string path = Path.Combine(root.FullName, "sparse.bin");
            using (var stream = File.Create(path))
            {
                Assert.True(DeviceIoControl(stream.SafeFileHandle, setSparse, IntPtr.Zero, 0,
                    IntPtr.Zero, 0, out _, IntPtr.Zero));
                stream.SetLength(logicalSize);
                stream.Position = logicalSize / 2;
                byte[] data = new byte[64 * 1024];
                Array.Fill(data, (byte)42);
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }

            var scan = await new DiskScanner().ScanAsync(root.FullName, null, default);
            var result = await new AllocatedSpaceInspector().InspectAsync(scan.Hierarchy!);
            Assert.Equal(1, result.MeasuredPaths);
            Assert.Equal(0, result.UnavailablePaths);
            Assert.Equal(logicalSize, result.MeasuredLogicalBytes);
            Assert.InRange(result.Bytes, 1, logicalSize - 1);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, uint inputSize,
        IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string link, string existingFile, IntPtr security);
}
