using Discoscope.Core.Models;

namespace Discoscope.Core.Services;

public sealed record FileTypeTotal(string Name, long Files, long LogicalBytes);
public sealed record FileDistributionTotals(IReadOnlyList<FileTypeTotal> Types, IReadOnlyList<FileTypeTotal> Ages);

public static class FileTypeDistribution
{
    private static readonly string[] Labels =
        ["Documents", "Images", "Vidéo", "Audio", "Archives", "Applications et bibliothèques",
         "Code et configuration", "Images disque", "Autres extensions", "Sans extension"];
    private static readonly string[] AgeLabels =
        ["30 jours ou moins", "31 à 365 jours", "Plus d’un an", "Date inconnue"];

    public static IReadOnlyList<FileTypeTotal> Analyze(ScanDirectory? root, CancellationToken token = default) =>
        AnalyzeDistributions(root, DateTime.UtcNow, token).Types;

    public static FileDistributionTotals AnalyzeDistributions(ScanDirectory? root, DateTime referenceUtc,
        CancellationToken token = default)
    {
        if (root is null) return new([], []);
        var totals = new (long Files, long Bytes)[Labels.Length];
        var ages = new (long Files, long Bytes)[AgeLabels.Length];
        var recentCutoff = referenceUtc.AddDays(-30);
        var oldCutoff = referenceUtc.AddDays(-365);
        foreach (var directory in root.EnumerateSubtree())
        {
            token.ThrowIfCancellationRequested();
            foreach (var file in directory.Files)
            {
                token.ThrowIfCancellationRequested();
                ref var total = ref totals[Category(file.Name)];
                total.Files++;
                total.Bytes += file.Bytes;
                int age = file.ModifiedUtc == default ? 3
                    : file.ModifiedUtc >= recentCutoff ? 0
                    : file.ModifiedUtc >= oldCutoff ? 1 : 2;
                ages[age].Files++;
                ages[age].Bytes += file.Bytes;
            }
        }
        return new(
            ToRows(totals, Labels).OrderByDescending(total => total.LogicalBytes).ToArray(),
            ToRows(ages, AgeLabels).ToArray());
    }

    private static IEnumerable<FileTypeTotal> ToRows((long Files, long Bytes)[] totals, string[] labels) =>
        totals.Select((total, index) => new FileTypeTotal(labels[index], total.Files, total.Bytes))
            .Where(total => total.Files > 0);

    private static int Category(string name)
    {
        ReadOnlySpan<char> extension = Path.GetExtension(name.AsSpan());
        Span<char> lower = stackalloc char[8];
        if (extension.Length > lower.Length) return 8;
        extension.ToLowerInvariant(lower);
        return lower[..extension.Length] switch
        {
            ".pdf" or ".doc" or ".docx" or ".odt" or ".txt" or ".rtf" or ".xls" or ".xlsx" or
            ".ods" or ".ppt" or ".pptx" or ".csv" => 0,
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".tif" or ".tiff" or
            ".svg" or ".heic" or ".avif" or ".psd" => 1,
            ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".webm" or ".m4v" => 2,
            ".mp3" or ".wav" or ".flac" or ".aac" or ".m4a" or ".ogg" or ".opus" => 3,
            ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".xz" or ".cab" => 4,
            ".exe" or ".dll" or ".msi" or ".sys" or ".appx" or ".msix" => 5,
            ".cs" or ".cpp" or ".h" or ".js" or ".ts" or ".py" or ".rs" or ".json" or ".xml" or
            ".ini" or ".yaml" or ".yml" or ".html" or ".css" or ".md" or ".xaml" => 6,
            ".iso" or ".vhd" or ".vhdx" or ".vmdk" or ".img" => 7,
            "" => 9,
            _ => 8
        };
    }
}
