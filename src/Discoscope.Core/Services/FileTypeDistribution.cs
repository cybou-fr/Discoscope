using Discoscope.Core.Models;

namespace Discoscope.Core.Services;

public sealed record FileTypeTotal(string Name, long Files, long LogicalBytes);

public static class FileTypeDistribution
{
    private static readonly string[] Labels =
        ["Documents", "Images", "Vidéo", "Audio", "Archives", "Applications et bibliothèques",
         "Code et configuration", "Images disque", "Autres extensions", "Sans extension"];

    public static IReadOnlyList<FileTypeTotal> Analyze(ScanDirectory? root, CancellationToken token = default)
    {
        if (root is null) return [];
        var totals = new (long Files, long Bytes)[Labels.Length];
        Accumulate(root);
        foreach (var directory in DirectoryEntries.Traverse(root))
            Accumulate(directory);
        return totals.Select((total, index) => new FileTypeTotal(Labels[index], total.Files, total.Bytes))
            .Where(total => total.Files > 0).OrderByDescending(total => total.LogicalBytes).ToArray();

        void Accumulate(ScanDirectory directory)
        {
            token.ThrowIfCancellationRequested();
            foreach (var file in directory.Files)
            {
                token.ThrowIfCancellationRequested();
                ref var total = ref totals[Category(file.Name)];
                total.Files++;
                total.Bytes += file.Bytes;
            }
        }
    }

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
