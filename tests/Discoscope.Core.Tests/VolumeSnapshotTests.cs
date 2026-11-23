using Discoscope.Core.Models;
using Discoscope.Core.Services;
namespace Discoscope.Core.Tests;
public sealed class VolumeSnapshotTests
{
    [Fact]
    public void UsedSpace_IsCapacityMinusFreeSpace()
    {
        var snapshot = new VolumeSnapshot("C:", 1000, 250, 200);
        Assert.Equal(750, snapshot.UsedBytes);
    }

    [Theory]
    [InlineData(@"\??\C:\data\sample", @"C:\data\sample")]
    [InlineData(@"\Device\HarddiskVolume3", null)]
    [InlineData(@"\??\Volume{example}\", null)]
    public void ParsesFolderMappedToDriveLetter(string target, string? expected)
    {
        Assert.Equal(expected, VolumeInspector.ParseSubstitution(target));
    }
}
