namespace Discoscope.Core.Models;

public readonly record struct ScanFile(string Name, long Bytes)
{
    public DateTime ModifiedUtc { get; init; }
}

public sealed class ScanDirectory
{
    public ScanDirectory(string path, ScanDirectory? parent)
        : this(path, System.IO.Path.GetFileName(path) is { Length: > 0 } name ? name : path, parent) { }

    internal ScanDirectory(string path, string name, ScanDirectory? parent)
    {
        Path = path;
        Name = name;
        Parent = parent;
    }

    public string Path { get; }
    public string Name { get; }
    public ScanDirectory? Parent { get; }
    public IReadOnlyList<ScanDirectory> Directories { get; internal set; } = [];
    public IReadOnlyList<ScanFile> Files { get; internal set; } = [];
    public long DirectBytes { get; internal set; }
    public long DirectFileCount { get; internal set; }
    public long Bytes { get; internal set; }
    public long FileCount { get; internal set; }
    public long DirectoryCount { get; internal set; }
    internal long SkippedCount { get; set; }
    internal bool EnumerationSucceeded { get; set; }
}
