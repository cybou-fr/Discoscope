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
        ScanAsync(root, progress, retainFiles: true, cancellationToken);

    internal Task<ScanResult> ScanAsync(
        string root, IProgress<ScanProgress>? progress, bool retainFiles, CancellationToken cancellationToken) =>
        Task.Run(() => Scan(root, progress, retainFiles, cancellationToken), cancellationToken);

    private static ScanResult Scan(
        string root, IProgress<ScanProgress>? progress, bool retainFiles, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            throw new DirectoryNotFoundException("Le dossier ou lecteur sélectionné est introuvable.");

        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Le dossier sélectionné est un lien ou un point de réanalyse.");
        var rootDirectory = new ScanDirectory(root, null);
        var stack = new Stack<ScanDirectory>();
        var visited = new List<ScanDirectory>();
        var issues = new List<ScanIssue>();
        const int maxLargestFiles = 200;
        var largestFiles = new PriorityQueue<LargeFileEntry, long>();
        stack.Push(rootDirectory);

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
            var directory = stack.Pop();
            var current = directory.Path;
            visited.Add(directory);
            var directories = new List<ScanDirectory>();
            List<ScanFile>? files = retainFiles ? [] : null;
            directoryCount++;
            long localBytes = 0, localFiles = 0;

            try
            {
                var children = new FileSystemEnumerable<(string Name, FileAttributes Attributes, long Length)>(
                    current, (ref FileSystemEntry entry) => (entry.FileName.ToString(), entry.Attributes, entry.Length),
                    enumerationOptions);
                foreach (var (name, attributes, length) in children)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        issues.Add(new ScanIssue(Path.Join(current, name), "Lien ou point de réanalyse ignoré."));
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        var child = new ScanDirectory(Path.Join(current, name), directory);
                        directories.Add(child);
                        stack.Push(child);
                    }
                    else
                    {
                        files?.Add(new ScanFile(name, length));
                        if (largestFiles.Count < maxLargestFiles)
                            largestFiles.Enqueue(new LargeFileEntry(Path.Join(current, name), length), length);
                        else if (largestFiles.TryPeek(out _, out long smallest) && length > smallest)
                            largestFiles.DequeueEnqueue(new LargeFileEntry(Path.Join(current, name), length), length);
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

            directory.Directories = directories.ToArray();
            directory.Files = files?.ToArray() ?? [];
            directory.DirectBytes = directory.Bytes = localBytes;
            directory.DirectFileCount = directory.FileCount = localFiles;
            ReportProgress(current);
        }

        // Each parent was visited before its children. Reverse order aggregates in linear time.
        for (int i = visited.Count - 1; i > 0; i--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = visited[i];
            var parent = directory.Parent!;
            parent.Bytes += directory.Bytes;
            parent.FileCount += directory.FileCount;
            parent.DirectoryCount += directory.DirectoryCount + 1;
        }

        var entries = visited.Skip(1)
            .Select(directory => new ScanEntry(directory.Path, directory.Bytes, directory.FileCount, directory.DirectoryCount)
            {
                DirectBytes = directory.DirectBytes,
                DirectFileCount = directory.DirectFileCount
            })
            .OrderByDescending(entry => entry.Bytes)
            .ToArray();

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new ScanProgress(fileCount, directoryCount, bytes, root));
        cancellationToken.ThrowIfCancellationRequested();
        return new ScanResult(root, bytes, fileCount, directoryCount, entries, issues, DateTimeOffset.Now)
        {
            LargestFiles = largestFiles.UnorderedItems.Select(x => x.Element).OrderByDescending(x => x.Bytes).ToArray(),
            Hierarchy = retainFiles ? rootDirectory : null,
            RootDirectBytes = rootDirectory.DirectBytes,
            RootDirectFileCount = rootDirectory.DirectFileCount
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
