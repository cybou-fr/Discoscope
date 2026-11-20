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

        root = Path.GetFullPath(root);
        var stack = new Stack<string>();
        var sizes = new Dictionary<string, (long Bytes, long Files, long Directories)>(StringComparer.OrdinalIgnoreCase);
        var issues = new List<ScanIssue>();
        stack.Push(root);

        long fileCount = 0, directoryCount = 0, bytes = 0;
        var lastReport = Environment.TickCount64;

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = stack.Pop();
            directoryCount++;
            long localBytes = 0, localFiles = 0, localDirectories = 0;

            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(current))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            issues.Add(new ScanIssue(path, "Lien ou point de réanalyse ignoré."));
                            continue;
                        }

                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            stack.Push(path);
                            localDirectories++;
                        }
                        else
                        {
                            long length = new FileInfo(path).Length;
                            localBytes += length;
                            localFiles++;
                            bytes += length;
                            fileCount++;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    {
                        issues.Add(new ScanIssue(path, ex.Message));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                issues.Add(new ScanIssue(current, ex.Message));
            }

            sizes[current] = (localBytes, localFiles, localDirectories);
            if (Environment.TickCount64 - lastReport >= 150)
            {
                progress?.Report(new ScanProgress(fileCount, directoryCount, bytes, current));
                lastReport = Environment.TickCount64;
            }
        }

        // Walk bottom-up so that each directory includes all descendant file sizes.
        foreach (var directory in sizes.Keys.OrderByDescending(p => p.Length).ToArray())
        {
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
            .Where(kv => !kv.Key.Equals(root, StringComparison.OrdinalIgnoreCase))
            .Select(kv => new ScanEntry(kv.Key, kv.Value.Bytes, kv.Value.Files, kv.Value.Directories))
            .OrderByDescending(e => e.Bytes)
            .ToArray();

        progress?.Report(new ScanProgress(fileCount, directoryCount, bytes, root));
        return new ScanResult(root, bytes, fileCount, directoryCount, entries, issues, DateTimeOffset.Now);
    }
}
