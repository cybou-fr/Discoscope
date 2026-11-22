using System.Runtime.InteropServices;
using Discoscope.Core.Services;

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

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string link, string existingFile, IntPtr security);
}
