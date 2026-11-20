namespace Discoscope.Core.Models;
public sealed record TemporaryLocation(string Name, string Path, long LogicalBytes, long FileCount, long SkippedCount, bool Accessible);
