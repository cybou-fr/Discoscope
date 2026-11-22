using Discoscope.Core.Models;

namespace Discoscope.Core.Services;

public enum FileTypeCategory
{
    Documents, Images, Video, Audio, Archives, Applications, Code, DiskImages, Other, NoExtension
}

public enum FileAgeBand { Recent, Intermediate, Old, Unknown }

public sealed record FileTypeTotal(string Name, long Files, long LogicalBytes)
{
    public FileAgeBand? AgeBand { get; init; }
    public FileTypeCategory? TypeCategory { get; init; }
}
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
                ref var total = ref totals[(int)ClassifyType(file.Name)];
                total.Files++;
                total.Bytes += file.Bytes;
                int age = (int)ClassifyAge(file.ModifiedUtc, recentCutoff, oldCutoff);
                ages[age].Files++;
                ages[age].Bytes += file.Bytes;
            }
        }
        return new(
            totals.Select((total, index) => new FileTypeTotal(Labels[index], total.Files, total.Bytes)
            {
                TypeCategory = (FileTypeCategory)index
            }).Where(total => total.Files > 0).OrderByDescending(total => total.LogicalBytes).ToArray(),
            ages.Select((total, index) => new FileTypeTotal(AgeLabels[index], total.Files, total.Bytes)
            {
                AgeBand = (FileAgeBand)index
            }).Where(total => total.Files > 0).ToArray());
    }

    public static FileAgeBand ClassifyAge(DateTime modifiedUtc, DateTime recentCutoff, DateTime oldCutoff) =>
        modifiedUtc == default ? FileAgeBand.Unknown
        : modifiedUtc >= recentCutoff ? FileAgeBand.Recent
        : modifiedUtc >= oldCutoff ? FileAgeBand.Intermediate : FileAgeBand.Old;

    public static FileTypeCategory ClassifyType(string name)
    {
        ReadOnlySpan<char> extension = Path.GetExtension(name.AsSpan());
        Span<char> lower = stackalloc char[8];
        if (extension.Length > lower.Length) return FileTypeCategory.Other;
        extension.ToLowerInvariant(lower);
        return lower[..extension.Length] switch
        {
            ".pdf" or ".doc" or ".docx" or ".odt" or ".txt" or ".rtf" or ".xls" or ".xlsx" or
            ".ods" or ".ppt" or ".pptx" or ".csv" => FileTypeCategory.Documents,
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".tif" or ".tiff" or
            ".svg" or ".heic" or ".avif" or ".psd" => FileTypeCategory.Images,
            ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".webm" or ".m4v" => FileTypeCategory.Video,
            ".mp3" or ".wav" or ".flac" or ".aac" or ".m4a" or ".ogg" or ".opus" => FileTypeCategory.Audio,
            ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".xz" or ".cab" => FileTypeCategory.Archives,
            ".exe" or ".dll" or ".msi" or ".sys" or ".appx" or ".msix" => FileTypeCategory.Applications,
            ".cs" or ".cpp" or ".h" or ".js" or ".ts" or ".py" or ".rs" or ".json" or ".xml" or
            ".ini" or ".yaml" or ".yml" or ".html" or ".css" or ".md" or ".xaml" => FileTypeCategory.Code,
            ".iso" or ".vhd" or ".vhdx" or ".vmdk" or ".img" => FileTypeCategory.DiskImages,
            "" => FileTypeCategory.NoExtension,
            _ => FileTypeCategory.Other
        };
    }
}
