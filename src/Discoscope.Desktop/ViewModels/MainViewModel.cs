using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Discoscope.Core.Models;
using Discoscope.Core.Services;

namespace Discoscope.Desktop.ViewModels;

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
        return $"{value:N1} {units[unit]}";
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
    private string _componentStatus = "Non analysé";
    private string _componentReport = "L’analyse DISM est disponible après le parcours du disque.";

    public ObservableCollection<string> Drives { get; } = new(
        DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.RootDirectory.FullName));

    public ObservableCollection<DirectoryRow> Entries { get; } = [];
    public ObservableCollection<TemporaryRow> TemporaryEntries { get; } = [];
    public string? SelectedDrive { get => _selectedDrive; set { _selectedDrive = value; Changed(); ScanCommand.RaiseCanExecuteChanged(); } }
    public string Status { get => _status; private set { _status = value; Changed(); } }
    public string FilesLabel { get => _filesLabel; private set { _filesLabel = value; Changed(); } }
    public string SizeLabel { get => _sizeLabel; private set { _sizeLabel = value; Changed(); } }
    public string VolumeLabel { get => _volumeLabel; private set { _volumeLabel = value; Changed(); } }
    public string FreeLabel { get => _freeLabel; private set { _freeLabel = value; Changed(); } }
    public string ComponentStatus { get => _componentStatus; private set { _componentStatus = value; Changed(); } }
    public string ComponentReport { get => _componentReport; private set { _componentReport = value; Changed(); } }
    public string IssuesLabel { get => _issuesLabel; private set { _issuesLabel = value; Changed(); } }

    public RelayCommand ScanCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand AnalyzeWindowsCommand { get; }

    public MainViewModel(IDiskScanner scanner)
    {
        _scanner = scanner;
        _selectedDrive = Drives.FirstOrDefault();
        ScanCommand = new RelayCommand(() => _ = ScanAsync(), () => !_isScanning && SelectedDrive is not null);
        CancelCommand = new RelayCommand(() => _scanCancellation?.Cancel(), () => _isScanning);
        AnalyzeWindowsCommand = new RelayCommand(() => _ = AnalyzeWindowsAsync(), () => !_isScanning);
    }

    private async Task AnalyzeWindowsAsync()
    {
        if (_isScanning) return;
        _isScanning = true;
        ScanCommand.RaiseCanExecuteChanged();
        AnalyzeWindowsCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        _scanCancellation = new CancellationTokenSource();
        ComponentStatus = "Analyse DISM en cours…";
        ComponentReport = "";
        try
        {
            var result = await new WindowsComponentInspector().InspectAsync(_scanCancellation.Token);
            ComponentStatus = result.Status;
            ComponentReport = result.RawOutput ?? result.Details;
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
            ScanCommand.RaiseCanExecuteChanged();
            AnalyzeWindowsCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task ScanAsync()
    {
        if (_isScanning || SelectedDrive is null) return;
        _isScanning = true;
        ScanCommand.RaiseCanExecuteChanged();
        AnalyzeWindowsCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        _scanCancellation = new CancellationTokenSource();
        Entries.Clear();
        TemporaryEntries.Clear();
        try
        {
            var volume = new VolumeInspector().Inspect(SelectedDrive);
            VolumeLabel = "Occupé : " + SizeFormatter.Format(volume.UsedBytes);
            FreeLabel = "Libre : " + SizeFormatter.Format(volume.FreeBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            VolumeLabel = FreeLabel = "Indisponible";
        }
        FilesLabel = SizeLabel = IssuesLabel = "…";
        Status = "Analyse en cours…";
        var progress = new Progress<ScanProgress>(p =>
        {
            FilesLabel = p.Files.ToString("N0");
            SizeLabel = SizeFormatter.Format(p.Bytes);
            Status = $"Analyse : {p.CurrentPath}";
        });

        try
        {
            var result = await _scanner.ScanAsync(SelectedDrive, progress, _scanCancellation.Token);
            FilesLabel = result.FileCount.ToString("N0");
            SizeLabel = SizeFormatter.Format(result.TotalBytes);
            IssuesLabel = result.Issues.Count.ToString("N0");
            foreach (var temp in await new TemporaryFilesInspector().InspectAsync(_scanCancellation.Token))
                TemporaryEntries.Add(new TemporaryRow(temp.Name, temp.Path, SizeFormatter.Format(temp.LogicalBytes), temp.FileCount, temp.SkippedCount));
            foreach (var entry in result.Entries.Take(300))
                Entries.Add(new DirectoryRow(entry.Path, entry.Bytes, entry.FileCount));
            Status = $"Analyse terminée à {result.CompletedAt:HH:mm} · {result.DirectoryCount:N0} dossiers · {result.Issues.Count:N0} avertissements.";
        }
        catch (OperationCanceledException)
        {
            Status = "Analyse annulée.";
        }
        catch (Exception ex)
        {
            Status = $"Échec de l'analyse : {ex.Message}";
        }
        finally
        {
            _scanCancellation.Dispose();
            _scanCancellation = null;
            _isScanning = false;
            ScanCommand.RaiseCanExecuteChanged();
            AnalyzeWindowsCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
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
