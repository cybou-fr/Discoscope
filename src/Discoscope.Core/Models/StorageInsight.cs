namespace Discoscope.Core.Models;

public sealed record StorageInsight(string Category, string Path, long LogicalBytes, long FileCount, string Guidance, string Risk);
