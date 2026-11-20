using System.Collections;

namespace Discoscope.Core.Models;

// Compatibility view for callers that need the full sorted flat list.
internal sealed class DirectoryEntries(ScanDirectory root) : IReadOnlyList<ScanEntry>
{
    private readonly Lazy<ScanEntry[]> _sorted = new(() => Traverse(root)
        .OrderByDescending(directory => directory.Bytes).Select(ToEntry).ToArray());

    public int Count => checked((int)root.DirectoryCount);
    public ScanEntry this[int index] => _sorted.Value[index];
    public IEnumerator<ScanEntry> GetEnumerator() => ((IEnumerable<ScanEntry>)_sorted.Value).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal static IEnumerable<ScanDirectory> Traverse(ScanDirectory root)
    {
        var levels = new Stack<(IReadOnlyList<ScanDirectory> Children, int Index)>();
        var children = root.Directories;
        int index = children.Count - 1;
        while (true)
        {
            if (index < 0)
            {
                if (!levels.TryPop(out var previous)) yield break;
                (children, index) = previous;
                continue;
            }
            var directory = children[index--];
            yield return directory;
            if (directory.Directories.Count == 0) continue;
            levels.Push((children, index));
            children = directory.Directories;
            index = children.Count - 1;
        }
    }

    internal static ScanEntry ToEntry(ScanDirectory directory) =>
        new(directory.Path, directory.Bytes, directory.FileCount, directory.DirectoryCount)
        {
            DirectBytes = directory.DirectBytes,
            DirectFileCount = directory.DirectFileCount
        };
}
