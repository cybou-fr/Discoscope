using Discoscope.Core.Models;
namespace Discoscope.Core.Services;
/// <summary>Read-only inventory of Windows temporary locations.</summary>
public sealed class TemporaryFilesInspector
{
    public Task<IReadOnlyList<TemporaryLocation>> InspectAsync(CancellationToken token) => InspectAsync(null, token);

    public Task<IReadOnlyList<TemporaryLocation>> InspectAsync(ScanDirectory? hierarchy, CancellationToken token) =>
        InspectLocationsAsync([
            ("Temporaires utilisateur", Path.GetTempPath()),
            ("Temporaires Windows", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"))
        ], hierarchy, token);

    internal static async Task<IReadOnlyList<TemporaryLocation>> InspectLocationsAsync(
        IEnumerable<(string Name, string Path)> locations, ScanDirectory? hierarchy, CancellationToken token)
    {
        var result = new List<TemporaryLocation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, path) in locations)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path)) continue;
            var full = Path.GetFullPath(path);
            if (!seen.Add(Path.TrimEndingDirectorySeparator(full))) continue;
            if (hierarchy is not null && FindDirectory(hierarchy, full, token) is { } scanned)
            {
                result.Add(new TemporaryLocation(name, full, scanned.Bytes, scanned.FileCount, scanned.SkippedCount,
                    scanned.EnumerationSucceeded));
                continue;
            }
            if (!Directory.Exists(full)) {
                result.Add(new TemporaryLocation(name, full, 0, 0, 0, false));
                continue;
            }
            try
            {
                var scan = await new DiskScanner().ScanAsync(full, null, retainFiles: false, token);
                result.Add(new TemporaryLocation(name, full, scan.TotalBytes, scan.FileCount, scan.Issues.Count,
                    scan.RootEnumerationSucceeded));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                result.Add(new TemporaryLocation(name, full, 0, 0, 1, false));
            }
        }
        return result;
    }

    internal static ScanDirectory? FindDirectory(ScanDirectory root, string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var relative = Path.GetRelativePath(root.Path, Path.TrimEndingDirectorySeparator(path));
        if (relative == ".") return root;
        if (Path.IsPathRooted(relative)) return null;

        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
        {
            token.ThrowIfCancellationRequested();
            ScanDirectory? match = null;
            bool ambiguous = false;
            foreach (var child in current.Directories)
            {
                token.ThrowIfCancellationRequested();
                if (child.Name.Equals(segment, StringComparison.Ordinal))
                {
                    match = child;
                    ambiguous = false;
                    break;
                }
                if (OperatingSystem.IsWindows() && child.Name.Equals(segment, StringComparison.OrdinalIgnoreCase))
                {
                    ambiguous |= match is not null;
                    match = child;
                }
            }
            if (match is null || ambiguous) return null;
            current = match;
        }
        return current;
    }
}
