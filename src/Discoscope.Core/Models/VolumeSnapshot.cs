namespace Discoscope.Core.Models;
public sealed record VolumeSnapshot(string Root, long CapacityBytes, long FreeBytes, long AvailableBytes)
{
    public string? SubstitutedPath { get; init; }
    public long UsedBytes => CapacityBytes - FreeBytes;
}
