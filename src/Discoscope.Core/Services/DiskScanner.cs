using System.IO.Enumeration;
using Discoscope.Core.Models;

namespace Discoscope.Core.Services;

/// <summary>
/// Read-only, iterative directory scanner. Never follows directory reparse points.
/// File lengths are logical lengths, not unique NTFS allocation sizes.
/// </summary>
public sealed class DiskScanner : IDiskScanner
{
    public Task<ScanResult> ScanAsync(
        string root, IProgress<ScanProgress>? progress, CancellationToken cancellationToken) =>
        Task.Run(() => Scan(root, progress, cancellationToken), cancellationToken);

    private static ScanResult Scan(
        string root, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            throw new DirectoryNotFoundException("Le dossier ou lecteur sélectionné est introuvable.");

        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Le dossier sélectionné est un lien ou un point de réanalyse.");
        var stack = new Stack<string>();
        var sizes = new Dictionary<string, (long Bytes, long Files, long Directories)>(StringComparer.Ordinal);
        var issues = new List<ScanIssue>();
        const int maxLargestFiles = 200;
        var largestFiles = new PriorityQueue<LargeFileEntry, long>();
        stack.Push(root);

        long fileCount = 0, directoryCount = 0, bytes = 0;
        var lastReport = Environment.TickCount64;
        var enumerationOptions = new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = false
        };

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = stack.Pop();
            directoryCount++;
            long localBytes = 0, localFiles = 0;

            try
            {
                var children = new FileSystemEnumerable<(string Path, FileAttributes Attributes, long Length)>(
                    current, (ref FileSystemEntry entry) => (entry.ToFullPath(), entry.Attributes, entry.Length),
                    enumerationOptions);
                foreach (var (path, attributes, length) in children)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        issues.Add(new ScanIssue(path, "Lien ou point de réanalyse ignoré."));
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        stack.Push(path);
                    }
                    else
                    {
                        if (largestFiles.Count < maxLargestFiles)
                            largestFiles.Enqueue(new LargeFileEntry(path, length), length);
                        else if (largestFiles.TryPeek(out _, out long smallest) && length > smallest)
                            largestFiles.DequeueEnqueue(new LargeFileEntry(path, length), length);
                        localBytes += length;
                        localFiles++;
                        bytes += length;
                        fileCount++;
                    }
                    ReportProgress(current);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                issues.Add(new ScanIssue(current, ex.Message));
            }

            sizes[current] = (localBytes, localFiles, 0);
            ReportProgress(current);
        }

        // Walk bottom-up so that each directory includes all descendant file sizes.
        foreach (var directory in sizes.Keys.OrderByDescending(p => p.Length).ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory));
            if (parent is not null && sizes.TryGetValue(parent, out var ancestor))
            {
                var value = sizes[directory];
                sizes[parent] = (ancestor.Bytes + value.Bytes,
                    ancestor.Files + value.Files,
                    ancestor.Directories + value.Directories + 1);
            }
        }

        var entries = sizes
            .Where(kv => !kv.Key.Equals(root, StringComparison.Ordinal))
            .Select(kv => new ScanEntry(kv.Key, kv.Value.Bytes, kv.Value.Files, kv.Value.Directories))
            .OrderByDescending(e => e.Bytes)
            .ToArray();

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new ScanProgress(fileCount, directoryCount, bytes, root));
        cancellationToken.ThrowIfCancellationRequested();
        return new ScanResult(root, bytes, fileCount, directoryCount, entries, issues, DateTimeOffset.Now)
        {
            LargestFiles = largestFiles.UnorderedItems.Select(x => x.Element).OrderByDescending(x => x.Bytes).ToArray()
        };

        void ReportProgress(string current)
        {
            if (progress is null || Environment.TickCount64 - lastReport < 150)
                return;

            lastReport = Environment.TickCount64;
            progress.Report(new ScanProgress(fileCount, directoryCount, bytes, current));
        }
    }
}
