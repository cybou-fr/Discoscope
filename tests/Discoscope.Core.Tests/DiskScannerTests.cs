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

            var result = await new DiskScanner().ScanAsync(root, null, CancellationToken.None);

            Assert.Equal(192, result.TotalBytes);
            Assert.Equal(2, result.FileCount);
            Assert.Equal(3, result.DirectoryCount);
            Assert.Contains(result.Entries, x => x.Path == Path.Combine(root, "one") && x.Bytes == 192);
            Assert.Contains(result.Entries, x => x.Path == Path.Combine(root, "one", "two") && x.Bytes == 64);
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
}
