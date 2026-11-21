using System.Collections;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Discoscope.Core.Models;

namespace Discoscope.Desktop.ViewModels;

public sealed record ExplorerBreadcrumb(ScanDirectory Directory, bool IsCurrent, ParameterCommand NavigateCommand);

public sealed class ExplorerNode : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _isSelected;
    private ExplorerNode[]? _children;

    public ExplorerNode(ScanDirectory directory) => Directory = directory;

    public ScanDirectory? Directory { get; }
    public string Name => Directory?.Name ?? "";
    // A placeholder gives WPF an expander before the children are materialized.
    private static readonly ExplorerNode Placeholder = new();
    private ExplorerNode() { }
    public IReadOnlyList<ExplorerNode> Children => _children ??
        (Directory?.Directories.Count > 0 ? [Placeholder] : []);

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            if (value && _children is null && Directory is not null)
            {
                _children = Directory.Directories.Select(directory => new ExplorerNode(directory)).ToArray();
                PropertyChanged?.Invoke(this, new(nameof(Children)));
            }
            PropertyChanged?.Invoke(this, new(nameof(IsExpanded)));
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record ExplorerRow(ScanDirectory Parent, int Index, string Name, long Bytes, ScanDirectory? Directory)
{
    public DateTime ModifiedUtc { get; init; }
    public string ModifiedLabel => ModifiedUtc == default ? "—" : ModifiedUtc.ToLocalTime().ToString("d");
    public string FullPath => Directory?.Path ?? Path.Join(Parent.Path, Name);
    public string Type => Directory is not null ? "Dossier" : Path.GetExtension(Name) is { Length: > 0 } extension
        ? extension : "Fichier";
    public string SizeLabel => SizeFormatter.Format(Bytes);
}

public sealed class ExplorerRows(ScanDirectory directory, int[]? order = null) : IList
{
    public int Count => order?.Length ?? SourceCount;
    private int SourceCount => directory.Directories.Count + directory.Files.Count;

    [AllowNull]
    public object this[int index]
    {
        get
        {
            int sourceIndex = order?[index] ?? index;
            var child = GetDirectory(sourceIndex);
            var file = child is null ? directory.Files[sourceIndex - directory.Directories.Count] : default;
            return new ExplorerRow(directory, index, child?.Name ?? file.Name, child?.Bytes ?? file.Bytes, child) { ModifiedUtc = file.ModifiedUtc };
        }
        set => throw new NotSupportedException();
    }

    public ExplorerRows Sort(string property, ListSortDirection direction, CancellationToken token = default)
    {
        var indices = order is null ? Enumerable.Range(0, SourceCount).ToArray() : (int[])order.Clone();
        int comparisons = 0;
        try
        {
            Array.Sort(indices, (left, right) =>
            {
                if ((++comparisons & 1023) == 0) token.ThrowIfCancellationRequested();
                int comparison = property switch
                {
                    nameof(ExplorerRow.Bytes) => Bytes(left).CompareTo(Bytes(right)),
                    nameof(ExplorerRow.Type) => CompareTypes(left, right),
                    nameof(ExplorerRow.ModifiedUtc) => File(left).ModifiedUtc.CompareTo(File(right).ModifiedUtc),
                    _ => StringComparer.CurrentCultureIgnoreCase.Compare(Name(left), Name(right))
                };
                if (comparison == 0)
                    comparison = left.CompareTo(right);
                return direction == ListSortDirection.Ascending ? comparison : -Math.Sign(comparison);
            });
        }
        catch (InvalidOperationException ex) when (ex.InnerException is OperationCanceledException && token.IsCancellationRequested)
        {
            token.ThrowIfCancellationRequested();
        }
        token.ThrowIfCancellationRequested();
        return new ExplorerRows(directory, indices);
    }

    public ExplorerRows Filter(string name, string extension, long minimumBytes, DateTime? modifiedBefore,
        CancellationToken token)
    {
        if (name.Length == 0 && extension.Length == 0 && minimumBytes == 0 && modifiedBefore is null)
            return new ExplorerRows(directory);

        var matches = new List<int>();
        for (int index = 0; index < SourceCount; index++)
        {
            if ((index & 1023) == 0) token.ThrowIfCancellationRequested();
            string entryName = Name(index);
            if (!entryName.Contains(name, StringComparison.OrdinalIgnoreCase) || Bytes(index) < minimumBytes)
                continue;
            if (extension.Length > 0 && (GetDirectory(index) is not null ||
                !Path.GetExtension(entryName.AsSpan()).Equals(extension.AsSpan(), StringComparison.OrdinalIgnoreCase)))
                continue;
            if (modifiedBefore is { } cutoff && (File(index).ModifiedUtc == default || File(index).ModifiedUtc > cutoff))
                continue;
            matches.Add(index);
        }
        return new ExplorerRows(directory, matches.ToArray());
    }

    private ScanFile File(int index) =>
        GetDirectory(index) is null ? directory.Files[index - directory.Directories.Count] : default;

    private ScanDirectory? GetDirectory(int index) =>
        index < directory.Directories.Count ? directory.Directories[index] : null;

    private string Name(int index) => GetDirectory(index)?.Name ?? directory.Files[index - directory.Directories.Count].Name;
    private long Bytes(int index) => GetDirectory(index)?.Bytes ?? directory.Files[index - directory.Directories.Count].Bytes;

    private int CompareTypes(int left, int right)
    {
        bool leftIsDirectory = GetDirectory(left) is not null;
        bool rightIsDirectory = GetDirectory(right) is not null;
        if (leftIsDirectory || rightIsDirectory)
            return rightIsDirectory.CompareTo(leftIsDirectory);

        return Path.GetExtension(Name(left).AsSpan()).CompareTo(
            Path.GetExtension(Name(right).AsSpan()), StringComparison.OrdinalIgnoreCase);
    }

    public int IndexOf(object? value) => value is ExplorerRow row && row.Index >= 0 && row.Index < Count
        && row.Equals(this[row.Index]) ? row.Index : -1;
    public bool Contains(object? value) => IndexOf(value) >= 0;
    public IEnumerator GetEnumerator()
    {
        for (int i = 0; i < Count; i++)
            yield return this[i];
    }

    public void CopyTo(Array array, int index)
    {
        for (int i = 0; i < Count; i++)
            array.SetValue(this[i], index + i);
    }

    public bool IsReadOnly => true;
    public bool IsFixedSize => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
}
