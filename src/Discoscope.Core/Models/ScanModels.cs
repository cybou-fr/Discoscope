namespace Discoscope.Core.Models;

public sealed record ScanProgress(long Files, long Directories, long Bytes, string CurrentPath);
public sealed record ScanEntry(string Path, long Bytes, long FileCount, long DirectoryCount);
public sealed record LargeFileEntry(string Path, long Bytes);
public sealed record ScanIssue(string Path, string Message);
public sealed record ScanResult(
    string Root,
    long TotalBytes,
    long FileCount,
    long DirectoryCount,
    IReadOnlyList<ScanEntry> Entries,
    IReadOnlyList<ScanIssue> Issues,
    DateTimeOffset CompletedAt)
{
    public IReadOnlyList<LargeFileEntry> LargestFiles { get; init; } = [];
}
