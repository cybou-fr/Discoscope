using System.Diagnostics;
using System.IO.Enumeration;
using Discoscope.Core.Models;

namespace Discoscope.Core.Services;

/// <summary>
/// Read-only, iterative directory scanner. Never follows directory reparse points.
/// File lengths are logical lengths, not unique NTFS allocation sizes.
/// </summary>
public sealed class DiskScanner : IDiskScanner
{
    private static readonly ActivitySource Telemetry = new("Discoscope.Core.Scan");
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
        var stack = new Stack<(ScanDirectory Directory, bool Visited)>();
        var issues = new List<ScanIssue>();
        const int maxLargestFiles = 200;
        var largestFiles = new PriorityQueue<LargeFileEntry, long>();
        stack.Push((rootDirectory, false));

        long fileCount = 0, directoryCount = 0, bytes = 0;
        var lastReport = Environment.TickCount64;
        var enumerationOptions = new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = false
        };

        using var activity = Telemetry.StartActivity("scan.filesystem");
        long enumerationTicks = 0, aggregationTicks = 0;
        var started = Stopwatch.GetTimestamp();
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, visited) = stack.Pop();
            if (visited)
            {
                var aggregationStarted = activity is null ? 0 : Stopwatch.GetTimestamp();
                var parent = directory.Parent!;
                checked
                {
                    parent.Bytes += directory.Bytes;
                    parent.FileCount += directory.FileCount;
                    parent.DirectoryCount += directory.DirectoryCount + 1;
                    parent.SkippedCount += directory.SkippedCount;
                }
                if (activity is not null) aggregationTicks += Stopwatch.GetTimestamp() - aggregationStarted;
                continue;
            }

            var current = directory.Path;
            List<ScanDirectory>? directories = null;
            List<ScanFile>? files = null;
            int priorIssues = issues.Count;
            directoryCount++;
            long localBytes = 0, localFiles = 0;

            try
            {
                var enumerationStarted = activity is null ? 0 : Stopwatch.GetTimestamp();
                using var children = new FileSystemEnumerable<(string Name, FileAttributes Attributes, long Length, DateTime ModifiedUtc)>(
                    current, (ref FileSystemEntry entry) => (entry.FileName.ToString(), entry.Attributes, entry.Length, retainFiles && !entry.IsDirectory ? entry.LastWriteTimeUtc.UtcDateTime : default),
                    enumerationOptions).GetEnumerator();
                if (activity is not null) enumerationTicks += Stopwatch.GetTimestamp() - enumerationStarted;
                while (MoveNext(children))
                {
                    var (name, attributes, length, modifiedUtc) = children.Current;
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        issues.Add(new ScanIssue(Path.Join(current, name), "Lien ou point de réanalyse ignoré."));
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        var child = new ScanDirectory(Path.Join(current, name), name, directory);
                        (directories ??= []).Add(child);
                    }
                    else
                    {
                        if (retainFiles) (files ??= []).Add(new ScanFile(name, length) { ModifiedUtc = modifiedUtc });
                        if (largestFiles.Count < maxLargestFiles)
                            largestFiles.Enqueue(new LargeFileEntry(Path.Join(current, name), length), length);
                        else if (largestFiles.TryPeek(out _, out long smallest) && length > smallest)
                            largestFiles.DequeueEnqueue(new LargeFileEntry(Path.Join(current, name), length), length);
                        checked
                        {
                            localBytes += length;
                            localFiles++;
                            bytes += length;
                            fileCount++;
                        }
                    }
                    ReportProgress(current);
                }
                directory.EnumerationSucceeded = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                issues.Add(new ScanIssue(current, ex.Message));
            }

            directory.Directories = directories?.ToArray() ?? [];
            directory.Files = files?.ToArray() ?? [];
            directory.DirectBytes = directory.Bytes = localBytes;
            directory.DirectFileCount = directory.FileCount = localFiles;
            directory.SkippedCount = issues.Count - priorIssues;
            if (directory.Parent is not null) stack.Push((directory, true));
            foreach (var child in directory.Directories)
                stack.Push((child, false));
            ReportProgress(current);
        }

        if (activity is not null)
        {
            var elapsedTicks = Stopwatch.GetTimestamp() - started;
            activity.SetTag("enumeration.ms", Stopwatch.GetElapsedTime(0, enumerationTicks).TotalMilliseconds);
            activity.SetTag("aggregation.ms", Stopwatch.GetElapsedTime(0, aggregationTicks).TotalMilliseconds);
            activity.SetTag("hierarchy_and_bookkeeping.ms",
                Stopwatch.GetElapsedTime(0, elapsedTicks - enumerationTicks - aggregationTicks).TotalMilliseconds);
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new ScanProgress(fileCount, directoryCount, bytes, root));
        cancellationToken.ThrowIfCancellationRequested();
        return new ScanResult(root, bytes, fileCount, directoryCount, retainFiles ? new DirectoryEntries(rootDirectory) : [], issues, DateTimeOffset.Now)
        {
            LargestFiles = largestFiles.UnorderedItems.Select(x => x.Element).OrderByDescending(x => x.Bytes).ToArray(),
            Hierarchy = retainFiles ? rootDirectory : null,
            RootEnumerationSucceeded = rootDirectory.EnumerationSucceeded,
            RootDirectBytes = rootDirectory.DirectBytes,
            RootDirectFileCount = rootDirectory.DirectFileCount
        };

        bool MoveNext(IEnumerator<(string Name, FileAttributes Attributes, long Length, DateTime ModifiedUtc)> children)
        {
            if (activity is null) return children.MoveNext();

            var stamp = Stopwatch.GetTimestamp();
            try { return children.MoveNext(); }
            finally { enumerationTicks += Stopwatch.GetTimestamp() - stamp; }
        }

        void ReportProgress(string current)
        {
            if (progress is null || Environment.TickCount64 - lastReport < 150)
                return;

            lastReport = Environment.TickCount64;
            progress.Report(new ScanProgress(fileCount, directoryCount, bytes, current));
        }
    }
}
