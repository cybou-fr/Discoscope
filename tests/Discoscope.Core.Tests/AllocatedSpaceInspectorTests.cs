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

            string original = Path.Combine(root.FullName, "original.bin");
            string link = Path.Combine(root.FullName, "link.bin");
            string missing = Path.Combine(root.FullName, "missing.bin");
            File.WriteAllBytes(original, new byte[8192]);
            File.WriteAllBytes(missing, new byte[8192]);
            Assert.True(CreateHardLink(link, original, IntPtr.Zero));

            var scan = await new DiskScanner().ScanAsync(root.FullName, null, default);
            File.Delete(missing);
            var inspector = new AllocatedSpaceInspector();
            var result = await inspector.InspectAsync(scan.Hierarchy!);
            Assert.Equal(1, result.MeasuredPaths);
            Assert.Equal(1, result.UnavailablePaths);
            Assert.Equal(1, result.DuplicateLinks);
            Assert.Equal(8192, result.MeasuredLogicalBytes);
            Assert.InRange(result.Bytes, 8192, 65536);

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

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, uint inputSize,
        IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string link, string existingFile, IntPtr security);
}
