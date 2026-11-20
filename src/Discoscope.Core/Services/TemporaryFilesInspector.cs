using Discoscope.Core.Models;
namespace Discoscope.Core.Services;
/// <summary>Read-only inventory of Windows temporary locations.</summary>
public sealed class TemporaryFilesInspector
{
    public async Task<IReadOnlyList<TemporaryLocation>> InspectAsync(CancellationToken token)
    {
        var locations = new[] {
            ("Temporaires utilisateur", Path.GetTempPath()),
            ("Temporaires Windows", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"))
        };
        var result = new List<TemporaryLocation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, path) in locations)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path)) continue;
            var full = Path.GetFullPath(path);
            if (!seen.Add(full)) continue;
            if (!Directory.Exists(full)) {
                result.Add(new TemporaryLocation(name, full, 0, 0, 0, false));
                continue;
            }
            try
            {
                var scan = await new DiskScanner().ScanAsync(full, null, retainFiles: false, token);
                result.Add(new TemporaryLocation(name, full, scan.TotalBytes, scan.FileCount, scan.Issues.Count, true));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                result.Add(new TemporaryLocation(name, full, 0, 0, 1, false));
            }
        }
        return result;
    }
}
