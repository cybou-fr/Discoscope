using Discoscope.Core.Models;
namespace Discoscope.Core.Tests;
public sealed class VolumeSnapshotTests
{
    [Fact]
    public void UsedSpace_IsCapacityMinusFreeSpace()
    {
        var snapshot = new VolumeSnapshot("C:", 1000, 250, 200);
        Assert.Equal(750, snapshot.UsedBytes);
    }
}
