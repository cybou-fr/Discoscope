namespace Discoscope.Core.Models;

public readonly record struct ScanFile(string Name, long Bytes);

public sealed class ScanDirectory(string path, ScanDirectory? parent)
{
    public string Path { get; } = path;
    public string Name { get; } = System.IO.Path.GetFileName(path) is { Length: > 0 } name ? name : path;
    public ScanDirectory? Parent { get; } = parent;
    public IReadOnlyList<ScanDirectory> Directories { get; internal set; } = [];
    public IReadOnlyList<ScanFile> Files { get; internal set; } = [];
    public long DirectBytes { get; internal set; }
    public long DirectFileCount { get; internal set; }
    public long Bytes { get; internal set; }
    public long FileCount { get; internal set; }
    public long DirectoryCount { get; internal set; }
}
