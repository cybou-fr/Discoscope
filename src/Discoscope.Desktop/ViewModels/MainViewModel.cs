using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Discoscope.Core.Models;
using Discoscope.Core.Services;

namespace Discoscope.Desktop.ViewModels;

public sealed record LargeFileRow(string Path, long Bytes)
{
    public string SizeLabel => SizeFormatter.Format(Bytes);
    public string Extension => System.IO.Path.GetExtension(Path).ToLowerInvariant();
}

public sealed record StorageInsightRow(string Category, string Path, string Size, string Risk, string Guidance);
public sealed record FileTypeRow(string Name, long Files, long Bytes, double Share)
{
    public FileAgeBand? AgeBand { get; init; }
    public FileTypeCategory? TypeCategory { get; init; }
    public string SizeLabel => SizeFormatter.Format(Bytes);
    public string ShareLabel => Share.ToString("P1", CultureInfo.GetCultureInfo("fr-FR"));
}

public sealed record ScanIssueRow(string Path, string Message);

public sealed record TemporaryRow(string Name, string Path, string Size, long Files, long Skipped);

public sealed record DirectoryRow(string Path, long Bytes, long FileCount)
{
    public ScanDirectory? Directory { get; init; }
    public string SizeLabel => SizeFormatter.Format(Bytes);
}

internal static class SizeFormatter
{
    private static readonly string[] Units = ["o", "Kio", "Mio", "Gio", "Tio"];
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    public static string Format(long bytes)
    {
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return string.Create(French, $"{value:N1} {Units[unit]}");
    }
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private static readonly ActivitySource Telemetry = new("Discoscope.Desktop.Scan");
    private readonly IDiskScanner _scanner;
    private CancellationTokenSource? _scanCancellation;
    private string? _selectedDrive;
    private int _volumeRequest;
    private bool _isScanning;
    private string _status = "Prêt à analyser un lecteur.";
    private string _filesLabel = "—";
    private string _sizeLabel = "—";
    private string _issuesLabel = "—";
    private string _volumeLabel = "—";
    private string _freeLabel = "—";
    private string _mapPath = "Aucune analyse";
    private ScanDirectory? _currentDirectory;
    private ExplorerNode? _explorerRoot;
    private ExplorerNode? _selectedExplorerNode;
    private bool _synchronizingExplorer;
    private CancellationTokenSource? _explorerSortCancellation;
    private CancellationTokenSource? _navigationCancellation;
    private string _vssStatus = "Non analysé";
    private string _vssReport = "L’analyse VSS est disponible séparément.";
    private string _componentStatus = "Non analysé";
    private string _componentReport = "L’analyse DISM est disponible séparément.";

    public ObservableCollection<string> Drives { get; } = new(
        DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.RootDirectory.FullName));

    public ObservableCollection<DirectoryRow> Entries { get; } = [];
    public ObservableCollection<TemporaryRow> TemporaryEntries { get; } = [];
    public ObservableCollection<LargeFileRow> LargestFiles { get; } = [];
    public ObservableCollection<ScanIssueRow> ScanIssues { get; } = [];
    public ObservableCollection<StorageInsightRow> StorageInsights { get; } = [];
    public ObservableCollection<FileTypeRow> FileTypes { get; } = [];
    public ObservableCollection<FileTypeRow> FileAges { get; } = [];
    public DateTime? FileAgeReferenceUtc { get; private set; }
    public string CapacityLabel { get; private set; } = "Total : —";
    public IReadOnlyList<TreemapItem> MapEntries { get; private set; } = [];
    public ScanDirectory? CurrentDirectory => _currentDirectory;
    public IReadOnlyList<ExplorerBreadcrumb> ExplorerPath { get; private set; } = [];
    public string ExplorerContentSummary => _currentDirectory is { } directory
        ? $"{directory.Directories.Count:N0} {(directory.Directories.Count > 1 ? "dossiers" : "dossier")} · " +
          $"{directory.Files.Count:N0} {(directory.Files.Count > 1 ? "fichiers" : "fichier")}"
        : "";
    public IReadOnlyList<ExplorerNode> ExplorerRoots { get; private set; } = [];
    public ExplorerRows? ExplorerItems { get; private set; }
    public string MapHint => MapEntries.Count > 0 ? ""
        : _currentDirectory is null ? MapPath
        : _currentDirectory.Bytes == 0 ? "Aucune donnée de taille non nulle." : "Préparation de la carte…";
    public bool IsBusy => _isScanning;
    public bool CanSelectDrive => !IsBusy;
    public string MapPath { get => _mapPath; private set { _mapPath = value; Changed(); } }
    public RelayCommand NavigateBackCommand { get; }
    public ParameterCommand NavigateMapCommand { get; }
    public string? SelectedDrive
    {
        get => _selectedDrive;
        set
        {
            if (_selectedDrive == value) return;
            _selectedDrive = value;
            CapacityLabel = "Total : —";
            VolumeLabel = "Occupé : —";
            FreeLabel = "Libre : —";
            Changed(nameof(CapacityLabel));
            Changed();
            ScanCommand.RaiseCanExecuteChanged();
            _ = RefreshVolumeAsync(value);
        }
    }
    public string Status { get => _status; private set { _status = value; Changed(); } }
    public string FilesLabel { get => _filesLabel; private set { _filesLabel = value; Changed(); } }
    public string SizeLabel { get => _sizeLabel; private set { _sizeLabel = value; Changed(); } }
    public string VolumeLabel { get => _volumeLabel; private set { _volumeLabel = value; Changed(); } }
    public string FreeLabel { get => _freeLabel; private set { _freeLabel = value; Changed(); } }
    public string VssStatus { get => _vssStatus; private set { _vssStatus = value; Changed(); } }
    public string VssReport { get => _vssReport; private set { _vssReport = value; Changed(); } }
    public string ComponentStatus { get => _componentStatus; private set { _componentStatus = value; Changed(); } }
    public string ComponentReport { get => _componentReport; private set { _componentReport = value; Changed(); } }
    public string IssuesLabel { get => _issuesLabel; private set { _issuesLabel = value; Changed(); } }

    public RelayCommand ScanCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand AnalyzeWindowsCommand { get; }
    public RelayCommand AnalyzeVssCommand { get; }

    public MainViewModel(IDiskScanner scanner)
    {
        _scanner = scanner;
        _selectedDrive = Drives.FirstOrDefault();
        ScanCommand = new RelayCommand(() => _ = ScanAsync(), () => !_isScanning && SelectedDrive is not null);
        CancelCommand = new RelayCommand(() => _scanCancellation?.Cancel(), () => _isScanning);
        AnalyzeWindowsCommand = new RelayCommand(() => _ = AnalyzeWindowsAsync(), () => !_isScanning);
        AnalyzeVssCommand = new RelayCommand(() => _ = AnalyzeVssAsync(), () => !_isScanning);
        NavigateBackCommand = new RelayCommand(() => _ = ShowDirectoryAsync(_currentDirectory!.Parent!),
            () => !_isScanning && _currentDirectory?.Parent is not null);
        NavigateMapCommand = new ParameterCommand(p => _ = ShowDirectoryAsync((ScanDirectory)p!),
            p => !_isScanning && !_synchronizingExplorer && p is ScanDirectory directory
                && !ReferenceEquals(directory, _currentDirectory) && IsInCurrentScan(directory));
        _ = RefreshVolumeAsync(_selectedDrive);
    }

    private bool IsInCurrentScan(ScanDirectory directory)
    {
        while (directory.Parent is { } parent)
            directory = parent;
        return ReferenceEquals(directory, _explorerRoot?.Directory);
    }

    private async Task ShowDirectoryAsync(ScanDirectory directory)
    {
        _navigationCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _navigationCancellation = cancellation;
        try
        {
            SetDirectory(directory);
            var items = await Task.Run(() => TreemapLayout.Summarize(directory, cancellation.Token), cancellation.Token);
            if (!cancellation.IsCancellationRequested)
                SetMapEntries(items);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_navigationCancellation, cancellation))
                _navigationCancellation = null;
        }
    }

    private void SetDirectory(ScanDirectory? directory)
    {
        _currentDirectory = directory;
        var path = new Stack<ExplorerBreadcrumb>();
        for (var part = directory; part is not null; part = part.Parent)
            path.Push(new ExplorerBreadcrumb(part, ReferenceEquals(part, directory), NavigateMapCommand));
        ExplorerPath = path.ToArray();
        if (directory is not null) MapPath = directory.Path;
        Changed(nameof(CurrentDirectory));
        Changed(nameof(ExplorerPath));
        Changed(nameof(ExplorerContentSummary));
        _synchronizingExplorer = true;
        try { UpdateExplorer(directory); }
        finally { _synchronizingExplorer = false; }
        SetMapEntries([]);
        NavigateBackCommand.RaiseCanExecuteChanged();
        NavigateMapCommand.RaiseCanExecuteChanged();
    }

    private void UpdateExplorer(ScanDirectory? directory)
    {
        _explorerSortCancellation?.Cancel();
        ExplorerItems = directory is null ? null : new ExplorerRows(directory);
        Changed(nameof(ExplorerItems));
        if (directory is null || directory.Parent is null && _explorerRoot is null)
        {
            _explorerRoot = directory is null ? null : new ExplorerNode(directory);
            ExplorerRoots = _explorerRoot is null ? [] : [_explorerRoot];
            Changed(nameof(ExplorerRoots));
        }
        if (directory is null || _explorerRoot is null)
        {
            _selectedExplorerNode = null;
            return;
        }

        var trail = new Stack<ScanDirectory>();
        for (var part = directory; part.Parent is not null; part = part.Parent)
            trail.Push(part);
        var node = _explorerRoot;
        while (trail.TryPop(out var part))
        {
            node.IsExpanded = true;
            node = node.Children.First(child => ReferenceEquals(child.Directory, part));
        }
        if (_selectedExplorerNode is { } previous && !ReferenceEquals(previous, node))
            previous.IsSelected = false;
        _selectedExplorerNode = node;
        node.IsSelected = true;
    }

    public Task<bool> SortExplorerAsync(string property, ListSortDirection direction) =>
        TransformExplorerAsync((items, token) => items.Sort(property, direction, token));

    public Task<bool> FilterExplorerAsync(string name, string extension, long minimumBytes, DateTime? modifiedBefore,
        bool wholeVolume, FileAgeBand? ageBand, FileTypeCategory? typeCategory)
    {
        var reference = FileAgeReferenceUtc;
        return TransformExplorerAsync((items, token) =>
            items.Filter(name, extension, minimumBytes, modifiedBefore, token, wholeVolume, ageBand, reference, typeCategory));
    }

    private async Task<bool> TransformExplorerAsync(Func<ExplorerRows, CancellationToken, ExplorerRows> transform)
    {
        if (ExplorerItems is not { } items) return false;
        _explorerSortCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _explorerSortCancellation = cancellation;
        try
        {
            var sorted = await Task.Run(() => transform(items, cancellation.Token), cancellation.Token);
            if (cancellation.IsCancellationRequested) return false;
            ExplorerItems = sorted;
            Changed(nameof(ExplorerItems));
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            if (ReferenceEquals(_explorerSortCancellation, cancellation))
                _explorerSortCancellation = null;
        }
    }

    private void SetMapEntries(IReadOnlyList<TreemapItem> entries)
    {
        MapEntries = entries;
        Changed(nameof(MapEntries));
        Changed(nameof(MapHint));
    }

    private async Task AnalyzeVssAsync()
    {
        if (_isScanning) return;
        _scanCancellation = new CancellationTokenSource();
        _isScanning = true;
        RefreshCommands();
        VssStatus = "Analyse VSS en cours…";
        VssReport = "";
        try
        {
            var result = await new ShadowStorageInspector().InspectAsync(_scanCancellation.Token);
            VssStatus = result.Status;
            VssReport = result.RawOutput is { Length: > 0 }
                ? result.RawOutput + Environment.NewLine + result.Details
                : result.Details;
        }
        catch (OperationCanceledException)
        {
            VssStatus = "Analyse annulée";
        }
        catch (Exception ex)
        {
            VssStatus = "Erreur";
            VssReport = ex.Message;
        }
        finally
        {
            _scanCancellation.Dispose();
            _scanCancellation = null;
            _isScanning = false;
            RefreshCommands();
        }
    }

    private void RefreshCommands()
    {
        Changed(nameof(IsBusy));
        Changed(nameof(CanSelectDrive));
        ScanCommand.RaiseCanExecuteChanged();
        AnalyzeWindowsCommand.RaiseCanExecuteChanged();
        AnalyzeVssCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        NavigateBackCommand.RaiseCanExecuteChanged();
        NavigateMapCommand.RaiseCanExecuteChanged();
    }

    private async Task AnalyzeWindowsAsync()
    {
        if (_isScanning) return;
        _scanCancellation = new CancellationTokenSource();
        _isScanning = true;
        RefreshCommands();
        ComponentStatus = "Analyse DISM en cours…";
        ComponentReport = "";
        try
        {
            var result = await new WindowsComponentInspector().InspectAsync(_scanCancellation.Token);
            ComponentStatus = result.Status;
            ComponentReport = string.IsNullOrWhiteSpace(result.RawOutput)
                ? result.Details
                : result.Details + Environment.NewLine + result.RawOutput;
        }
        catch (OperationCanceledException)
        {
            ComponentStatus = "Analyse annulée";
        }
        catch (Exception ex)
        {
            ComponentStatus = "Erreur";
            ComponentReport = ex.Message;
        }
        finally
        {
            _scanCancellation.Dispose();
            _scanCancellation = null;
            _isScanning = false;
            RefreshCommands();
        }
    }

    private async Task RefreshVolumeAsync(string? root, CancellationToken token = default)
    {
        int request = ++_volumeRequest;
        if (root is null) return;

        VolumeSnapshot? volume = null;
        try { volume = await Task.Run(() => new VolumeInspector().Inspect(root), token); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or System.Security.SecurityException) { }

        if (request != _volumeRequest) return;
        CapacityLabel = volume is null ? "Total : —" : "Total : " + SizeFormatter.Format(volume.CapacityBytes);
        VolumeLabel = volume is null ? "Occupé : —" : "Occupé : " + SizeFormatter.Format(volume.UsedBytes);
        FreeLabel = volume is null ? "Libre : —" : "Libre : " + SizeFormatter.Format(volume.FreeBytes);
        Changed(nameof(CapacityLabel));
    }

    private async Task ScanAsync()
    {
        if (_isScanning || SelectedDrive is null) return;
        var root = SelectedDrive;
        using var operation = Telemetry.StartActivity("scan.total");
        _scanCancellation = new CancellationTokenSource();
        _isScanning = true;
        RefreshCommands();
        Entries.Clear();
        TemporaryEntries.Clear();
        LargestFiles.Clear();
        ScanIssues.Clear();
        StorageInsights.Clear();
        FileTypes.Clear();
        FileAges.Clear();
        FileAgeReferenceUtc = null;
        _navigationCancellation?.Cancel();
        MapPath = "Analyse en cours…";
        SetDirectory(null);
        FilesLabel = SizeLabel = IssuesLabel = "…";
        Status = "Analyse en cours…";
        var activeScan = _scanCancellation;
        bool acceptProgress = true;
        var progress = new Progress<ScanProgress>(p =>
        {
            if (!acceptProgress || !ReferenceEquals(_scanCancellation, activeScan)) return;
            FilesLabel = p.Files.ToString("N0");
            SizeLabel = SizeFormatter.Format(p.Bytes);
            Status = $"Analyse : {p.CurrentPath}";
        });
        try
        {
            await RefreshVolumeAsync(root, activeScan.Token);
            var result = await _scanner.ScanAsync(root, progress, activeScan.Token);
            acceptProgress = false;
            Status = "Analyse des emplacements temporaires…";
            var directory = result.Hierarchy;
            IReadOnlyList<TemporaryLocation> temporaryEntries;
            using (Telemetry.StartActivity("scan.temporary_locations"))
                temporaryEntries = await Task.Run(() => new TemporaryFilesInspector().InspectAsync(directory, activeScan.Token),
                    activeScan.Token);

            Status = "Préparation des résultats…";
            (IReadOnlyList<TreemapItem> Map, IEnumerable<DirectoryRow> Overview,
                IReadOnlyList<StorageInsight> Storage, FileDistributionTotals Distribution) prepared;
            using (Telemetry.StartActivity("scan.prepare_results"))
                prepared = await Task.Run(() =>
                    (directory is null ? [] : TreemapLayout.Summarize(directory, activeScan.Token),
                        (directory is null
                            ? result.GetLargestDirectories(300, activeScan.Token)
                                .Select(entry => new DirectoryRow(entry.Path, entry.Bytes, entry.FileCount))
                            : result.GetLargestDirectoryNodes(300, activeScan.Token)
                                .Select(node => new DirectoryRow(node.Path, node.Bytes, node.FileCount) { Directory = node }))
                            .ToArray(),
                        WindowsStorageInsights.Analyze(directory, activeScan.Token),
                        FileTypeDistribution.AnalyzeDistributions(directory, result.CompletedAt.UtcDateTime,
                            activeScan.Token)), activeScan.Token);
            activeScan.Token.ThrowIfCancellationRequested();
            using var population = Telemetry.StartActivity("scan.populate_ui");
            FilesLabel = result.FileCount.ToString("N0");
            SizeLabel = SizeFormatter.Format(result.TotalBytes);
            IssuesLabel = result.Issues.Count.ToString("N0");
            foreach (var type in prepared.Distribution.Types)
                FileTypes.Add(new FileTypeRow(type.Name, type.Files, type.LogicalBytes,
                    result.TotalBytes > 0 ? (double)type.LogicalBytes / result.TotalBytes : 0)
                {
                    TypeCategory = type.TypeCategory
                });
            FileAgeReferenceUtc = result.CompletedAt.UtcDateTime;
            foreach (var age in prepared.Distribution.Ages)
                FileAges.Add(new FileTypeRow(age.Name, age.Files, age.LogicalBytes,
                    result.TotalBytes > 0 ? (double)age.LogicalBytes / result.TotalBytes : 0)
                {
                    AgeBand = age.AgeBand
                });
            foreach (var insight in prepared.Storage)
                StorageInsights.Add(new StorageInsightRow(insight.Category, insight.Path,
                    insight.IsMeasured ? SizeFormatter.Format(insight.LogicalBytes) : "—", insight.Risk, insight.Guidance));
            foreach (var issue in result.Issues.Take(2000))
                ScanIssues.Add(new ScanIssueRow(issue.Path, issue.Message));
            foreach (var file in result.LargestFiles)
                LargestFiles.Add(new LargeFileRow(file.Path, file.Bytes));
            foreach (var temp in temporaryEntries)
                TemporaryEntries.Add(new TemporaryRow(temp.Name, temp.Path,
                    temp.Accessible ? SizeFormatter.Format(temp.LogicalBytes) : "Indisponible", temp.FileCount, temp.SkippedCount));
            SetDirectory(directory);
            SetMapEntries(prepared.Map);
            foreach (var entry in prepared.Overview)
                Entries.Add(entry);
            Status = $"Analyse terminée à {result.CompletedAt:HH:mm} · {result.DirectoryCount:N0} dossiers · {result.Issues.Count:N0} avertissements.";
        }
        catch (OperationCanceledException)
        {
            Status = "Analyse annulée.";
            MapPath = Status;
            SetMapEntries([]);
            FilesLabel = SizeLabel = IssuesLabel = "—";
        }
        catch (Exception ex)
        {
            Status = $"Échec de l'analyse : {ex.Message}";
            MapPath = "Analyse indisponible";
            SetMapEntries([]);
            FilesLabel = SizeLabel = IssuesLabel = "—";
        }
        finally
        {
            _scanCancellation.Dispose();
            _scanCancellation = null;
            _isScanning = false;
            RefreshCommands();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand(Action action, Func<bool> canExecute) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute();
    public void Execute(object? parameter) => action();
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class ParameterCommand(Action<object?> action, Predicate<object?> canExecute) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute(parameter);
    public void Execute(object? parameter) => action(parameter);
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
