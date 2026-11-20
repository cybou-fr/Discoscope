using System.Collections.ObjectModel;
using System.ComponentModel;
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

public sealed record TemporaryRow(string Name, string Path, string Size, long Files, long Skipped);

public sealed record DirectoryRow(string Path, long Bytes, long FileCount)
{
    public string SizeLabel => SizeFormatter.Format(Bytes);
}

internal static class SizeFormatter
{
    public static string Format(long bytes)
    {
        string[] units = ["o", "Kio", "Mio", "Gio", "Tio"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return string.Create(CultureInfo.GetCultureInfo("fr-FR"), $"{value:N1} {units[unit]}");
    }
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly IDiskScanner _scanner;
    private CancellationTokenSource? _scanCancellation;
    private string? _selectedDrive;
    private bool _isScanning;
    private string _status = "Prêt à analyser un lecteur.";
    private string _filesLabel = "—";
    private string _sizeLabel = "—";
    private string _issuesLabel = "—";
    private string _volumeLabel = "—";
    private string _freeLabel = "—";
    private string _mapPath = "Aucune analyse";
    private Dictionary<string, DirectoryRow[]> _mapIndex = new(StringComparer.Ordinal);
    private string _mapRoot = "";
    private string _vssStatus = "Non analysé";
    private string _vssReport = "L’analyse VSS est disponible séparément.";
    private string _componentStatus = "Non analysé";
    private string _componentReport = "L’analyse DISM est disponible séparément.";

    public ObservableCollection<string> Drives { get; } = new(
        DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.RootDirectory.FullName));

    public ObservableCollection<DirectoryRow> Entries { get; } = [];
    public ObservableCollection<TemporaryRow> TemporaryEntries { get; } = [];
    public ObservableCollection<LargeFileRow> LargestFiles { get; } = [];
    public IReadOnlyList<DirectoryRow> MapEntries { get; private set; } = [];
    public string MapHint => MapEntries.Count > 0 ? ""
        : _mapIndex.ContainsKey(MapPath) ? "Aucun sous-dossier de taille non nulle." : MapPath;
    public bool IsBusy => _isScanning;
    public bool CanSelectDrive => !IsBusy;
    public string MapPath { get => _mapPath; private set { _mapPath = value; Changed(); } }
    public RelayCommand NavigateBackCommand { get; }
    public ParameterCommand NavigateMapCommand { get; }
    public string? SelectedDrive { get => _selectedDrive; set { _selectedDrive = value; Changed(); ScanCommand.RaiseCanExecuteChanged(); } }
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
        NavigateBackCommand = new RelayCommand(NavigateBack, () => !_isScanning && _mapPath != _mapRoot && _mapIndex.ContainsKey(_mapPath));
        NavigateMapCommand = new ParameterCommand(p => { if (p is string path) ShowMap(path); }, p => !_isScanning && p is string path && _mapIndex.ContainsKey(path));
    }

    private void ShowMap(string path)
    {
        if (!_mapIndex.TryGetValue(path, out var children)) return;
        MapPath = path;
        SetMapEntries(children.Where(x => x.Bytes > 0).Take(24).ToArray());
        NavigateBackCommand.RaiseCanExecuteChanged();
        NavigateMapCommand.RaiseCanExecuteChanged();
    }

    private void SetMapEntries(IReadOnlyList<DirectoryRow> entries)
    {
        MapEntries = entries;
        Changed(nameof(MapEntries));
        Changed(nameof(MapHint));
    }

    private void NavigateBack()
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(MapPath));
        if (parent is not null && _mapIndex.ContainsKey(parent)) ShowMap(parent);
    }

    private async Task AnalyzeVssAsync()
    {
        if (_isScanning) return;
        _isScanning = true;
        RefreshCommands();
        _scanCancellation = new CancellationTokenSource();
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
        _isScanning = true;
        RefreshCommands();
        _scanCancellation = new CancellationTokenSource();
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

    private async Task ScanAsync()
    {
        if (_isScanning || SelectedDrive is null) return;
        var root = SelectedDrive;
        _isScanning = true;
        RefreshCommands();
        _scanCancellation = new CancellationTokenSource();
        Entries.Clear();
        TemporaryEntries.Clear();
        LargestFiles.Clear();
        _mapIndex.Clear();
        MapPath = "Analyse en cours…";
        SetMapEntries([]);
        try
        {
            var volume = await Task.Run(() => new VolumeInspector().Inspect(root));
            VolumeLabel = "Occupé : " + SizeFormatter.Format(volume.UsedBytes);
            FreeLabel = "Libre : " + SizeFormatter.Format(volume.FreeBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            VolumeLabel = FreeLabel = "Indisponible";
        }
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
            var result = await _scanner.ScanAsync(root, progress, activeScan.Token);
            acceptProgress = false;
            Status = "Analyse des emplacements temporaires…";
            var temporaryEntries = await new TemporaryFilesInspector().InspectAsync(activeScan.Token);
            var mapIndex = await Task.Run(() =>
            {
                var rows = result.Entries.Select(e => new DirectoryRow(e.Path, e.Bytes, e.FileCount)).ToArray();
                var index = rows.GroupBy(e => Path.GetDirectoryName(e.Path) ?? "", StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
                index.TryAdd(result.Root, []);
                foreach (var row in rows)
                {
                    activeScan.Token.ThrowIfCancellationRequested();
                    index.TryAdd(row.Path, []);
                }
                return index;
            }, activeScan.Token);
            activeScan.Token.ThrowIfCancellationRequested();
            FilesLabel = result.FileCount.ToString("N0");
            SizeLabel = SizeFormatter.Format(result.TotalBytes);
            IssuesLabel = result.Issues.Count.ToString("N0");
            foreach (var file in result.LargestFiles)
                LargestFiles.Add(new LargeFileRow(file.Path, file.Bytes));
            foreach (var temp in temporaryEntries)
                TemporaryEntries.Add(new TemporaryRow(temp.Name, temp.Path, SizeFormatter.Format(temp.LogicalBytes), temp.FileCount, temp.SkippedCount));
            _mapRoot = Path.TrimEndingDirectorySeparator(result.Root);
            _mapIndex = mapIndex;
            ShowMap(_mapRoot);
            foreach (var entry in result.Entries.Take(300))
                Entries.Add(new DirectoryRow(entry.Path, entry.Bytes, entry.FileCount));
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
