namespace Discoscope.Core.Models;

public sealed record ScanProgress(long Files, long Directories, long Bytes, string CurrentPath);
public sealed record ScanEntry(string Path, long Bytes, long FileCount, long DirectoryCount)
{
    public long DirectBytes { get; init; }
    public long DirectFileCount { get; init; }
}
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
    public long RootDirectBytes { get; init; }
    public long RootDirectFileCount { get; init; }
    public ScanDirectory? Hierarchy { get; init; }
    internal bool RootEnumerationSucceeded { get; init; }

    public IReadOnlyList<ScanEntry> GetLargestDirectories(int count, CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        token.ThrowIfCancellationRequested();
        if (count == 0) return [];
        return Hierarchy is null ? Entries.Take(count).ToArray()
            : GetLargestDirectoryNodes(count, token).Select(DirectoryEntries.ToEntry).ToArray();
    }

    public IReadOnlyList<ScanDirectory> GetLargestDirectoryNodes(int count, CancellationToken token = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        token.ThrowIfCancellationRequested();
        if (count == 0 || Hierarchy is null) return [];
        var largest = new PriorityQueue<ScanDirectory, (long Bytes, long Order)>();
        long order = 0;
        foreach (var directory in DirectoryEntries.Traverse(Hierarchy))
        {
            token.ThrowIfCancellationRequested();
            var priority = (directory.Bytes, --order);
            if (largest.Count < count)
                largest.Enqueue(directory, priority);
            else if (largest.TryPeek(out _, out var smallest) && priority.CompareTo(smallest) > 0)
                largest.DequeueEnqueue(directory, priority);
        }
        return largest.UnorderedItems.OrderByDescending(item => item.Priority)
            .Select(item => item.Element).ToArray();
    }
}
