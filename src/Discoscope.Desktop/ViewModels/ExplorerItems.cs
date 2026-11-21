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

public sealed class ExplorerRows : IList
{
    public const int VolumeSearchLimit = 10_000;

    private readonly ScanDirectory directory;
    private readonly int[]? order;
    private readonly Entry[]? volumeMatches;
    public long TotalMatches { get; }

    public ExplorerRows(ScanDirectory directory) : this(directory, null, null, null) { }

    private ExplorerRows(ScanDirectory directory, int[]? order, Entry[]? volumeMatches, long? totalMatches)
    {
        this.directory = directory;
        this.order = order;
        this.volumeMatches = volumeMatches;
        TotalMatches = totalMatches ?? Count;
    }

    private readonly record struct Entry(ScanDirectory Parent, int Index)
    {
        public ScanDirectory? Directory => Index < Parent.Directories.Count ? Parent.Directories[Index] : null;
        public ScanFile File => Index >= Parent.Directories.Count ? Parent.Files[Index - Parent.Directories.Count] : default;
        public string Name => Directory?.Name ?? File.Name;
        public long Bytes => Directory?.Bytes ?? File.Bytes;
    }

    private Entry GetEntry(int index) => volumeMatches is null ? new Entry(directory, index) : volumeMatches[index];
    public int Count => order?.Length ?? SourceCount;
    private int SourceCount => volumeMatches?.Length ?? directory.Directories.Count + directory.Files.Count;

    [AllowNull]
    public object this[int index]
    {
        get
        {
            int sourceIndex = order?[index] ?? index;
            var entry = GetEntry(sourceIndex);
            return new ExplorerRow(entry.Parent, index, entry.Name, entry.Bytes, entry.Directory)
            {
                ModifiedUtc = entry.File.ModifiedUtc
            };
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
        return new ExplorerRows(directory, indices, volumeMatches, TotalMatches);
    }

    public ExplorerRows Filter(string name, string extension, long minimumBytes, DateTime? modifiedBefore,
        CancellationToken token, bool wholeVolume = false)
    {
        token.ThrowIfCancellationRequested();
        if (name.Length == 0 && extension.Length == 0 && minimumBytes == 0 && modifiedBefore is null)
            return new ExplorerRows(directory);

        bool Matches(Entry entry)
        {
            if (!entry.Name.Contains(name, StringComparison.OrdinalIgnoreCase) || entry.Bytes < minimumBytes)
                return false;
            if (extension.Length > 0 && (entry.Directory is not null ||
                !Path.GetExtension(entry.Name.AsSpan()).Equals(extension.AsSpan(), StringComparison.OrdinalIgnoreCase)))
                return false;
            return modifiedBefore is not { } cutoff ||
                entry.File.ModifiedUtc != default && entry.File.ModifiedUtc <= cutoff;
        }

        if (!wholeVolume)
        {
            var matches = new List<int>();
            for (int index = 0; index < directory.Directories.Count + directory.Files.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                if (Matches(new Entry(directory, index))) matches.Add(index);
            }
            return new ExplorerRows(directory, matches.ToArray(), null, null);
        }

        var root = directory;
        while (root.Parent is { } parent) root = parent;
        var volume = new List<Entry>();
        long total = 0;
        foreach (var folder in root.EnumerateSubtree())
        {
            token.ThrowIfCancellationRequested();
            for (int index = 0; index < folder.Directories.Count + folder.Files.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var entry = new Entry(folder, index);
                if (!Matches(entry)) continue;
                total++;
                if (volume.Count < VolumeSearchLimit) volume.Add(entry);
            }
        }
        return new ExplorerRows(directory, null, volume.ToArray(), total);
    }

    private ScanFile File(int index) => GetEntry(index).File;
    private ScanDirectory? GetDirectory(int index) => GetEntry(index).Directory;
    private string Name(int index) => GetEntry(index).Name;
    private long Bytes(int index) => GetEntry(index).Bytes;

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
