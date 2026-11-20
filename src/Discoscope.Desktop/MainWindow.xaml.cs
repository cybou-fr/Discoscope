using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Discoscope.Core.Models;
using Discoscope.Core.Services;
using Discoscope.Desktop.ViewModels;
using Discoscope.Desktop.Services;

namespace Discoscope.Desktop;

public partial class MainWindow : Window
{
    private bool _cleanupBusy;
    private bool _closeAfterCleanup;
    private int _cleanupProgressVersion;
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_cleanupBusy)
        {
            _closeAfterCleanup = true;
            _cleanupCancellation?.Cancel();
            e.Cancel = true;
            CleanupStatus.Text = "Annulation en cours avant la fermeture…";
            return;
        }
        base.OnClosing(e);
        if (!e.Cancel) ReleaseCleanupInventory();
    }
    private UserTempCleanup.Inventory? _cleanupInventory;
    private int _cleanupPage;

    private void ReleaseCleanupInventory()
    {
        CleanupFiles.ItemsSource = null;
        _cleanupInventory?.Dispose();
        _cleanupInventory = null;
        _cleanupPage = 0;
        UpdateCleanupPaging();
    }

    private void UpdateCleanupPaging()
    {
        if (CleanupPreviousPage is null || CleanupNextPage is null || CleanupPageStatus is null) return;
        var pages = _cleanupInventory?.PageCount ?? 0;
        CleanupPreviousPage.IsEnabled = !_cleanupBusy && _cleanupPage > 0;
        CleanupNextPage.IsEnabled = !_cleanupBusy && _cleanupPage + 1 < pages;
        CleanupPageStatus.Text = pages == 0 ? "Aucune page" : $"Page {_cleanupPage + 1:N0} / {pages:N0}";
    }

    private void ShowCleanupPage(int page)
    {
        if (_cleanupInventory is null || page < 0 || page >= _cleanupInventory.PageCount) return;
        var items = _cleanupInventory.ReadPage(page);
        _cleanupPage = page;
        CleanupFiles.ItemsSource = items;
        ShowCleanupInventoryStatus();
        UpdateCleanupPaging();
    }

    private void PreviousCleanupPage(object sender, RoutedEventArgs e) => ChangeCleanupPage(-1);
    private void NextCleanupPage(object sender, RoutedEventArgs e) => ChangeCleanupPage(1);

    private void ChangeCleanupPage(int step)
    {
        if (_cleanupBusy) return;
        try { ShowCleanupPage(_cleanupPage + step); }
        catch (IOException ex)
        {
            ReleaseCleanupInventory();
            UpdateCleanupSelection();
            CleanupStatus.Text = "Inventaire illisible. Relancez la recherche.";
            ShowCleanupMessage(ex.Message);
        }
    }

    private sealed record CleanupCategoryRow(string Name, long? Count, string Size, UserTempCleanup.Location Location);

    private void CleanupCategorySelected(object sender, SelectionChangedEventArgs e)
    {
        if (_cleanupBusy || CleanupCategories.SelectedItem is not CleanupCategoryRow row) return;
        CleanupLocation.SelectedIndex = row.Location == UserTempCleanup.Location.User ? 0 : 1;
        CleanupStatus.Text = "Catégorie choisie. Cliquez sur Rechercher pour voir les fichiers.";
    }
    private CancellationTokenSource? _cleanupCancellation;
    private UserTempCleanup.Location SelectedCleanupLocation =>
        CleanupLocation.SelectedIndex == 1
            ? UserTempCleanup.Location.Windows
            : UserTempCleanup.Location.User;

    private void CleanupLocationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CleanupFiles is null || CleanupStatus is null) return;
        ReleaseCleanupInventory();
        CleanupStatus.Text = "Recherchez les fichiers de cet emplacement.";
        if (CleanupPreviewButton is not null && CleanupRecycleButton is not null && CleanupSelectAllButton is not null && CleanupOverviewButton is not null && CleanupCancelButton is not null)
            UpdateCleanupSelection();
    }


    private void CleanupSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CleanupRecycleAllButton is not null && CleanupFiles is not null)
        {
            UpdateCleanupSelection();
            if (!_cleanupBusy && CleanupFiles.SelectedItems.Count == 0)
                ShowCleanupInventoryStatus();
        }
    }

    private void ShowCleanupInventoryStatus()
    {
        if (_cleanupInventory is { } inventory)
            CleanupStatus.Text = inventory.Count == 0
                ? "Aucun fichier éligible dans cet emplacement."
                : $"{inventory.Count:N0} fichiers · {FormatCleanupBytes(inventory.LogicalBytes)} (taille logique). Sélection par page.";
    }

    private void CancelCleanup(object sender, RoutedEventArgs e)
    {
        _cleanupCancellation?.Cancel();
        CleanupCancelButton.IsEnabled = false;
        CleanupStatus.Text = "Annulation en cours…";
    }

    private IProgress<T> CleanupProgress<T>(Action<T> update)
    {
        var operation = _cleanupCancellation;
        int version = ++_cleanupProgressVersion;
        return new Progress<T>(value =>
        {
            if (version == _cleanupProgressVersion && operation is { IsCancellationRequested: false }
                && ReferenceEquals(_cleanupCancellation, operation))
                update(value);
        });
    }

    private void FinishCleanup()
    {
        var operation = _cleanupCancellation;
        _cleanupCancellation = null;
        operation?.Dispose();
        _cleanupBusy = false;
        CleanupProgressBar.Visibility = Visibility.Hidden;
        UpdateCleanupSelection();
        if (_closeAfterCleanup) Dispatcher.BeginInvoke(new Action(Close));
    }

    private CancellationToken StartCleanup()
    {
        _cleanupBusy = true;
        var cancellation = new CancellationTokenSource();
        _cleanupCancellation = cancellation;
        CleanupProgressBar.IsIndeterminate = true;
        CleanupProgressBar.Visibility = Visibility.Visible;
        UpdateCleanupSelection();
        return cancellation.Token;
    }

    private void ShowCleanupMessage(string message)
    {
        _cleanupProgressVersion++;
        if (!_closeAfterCleanup) MessageBox.Show(this, message, "Discoscope");
    }

    private void UpdateCleanupSelection()
    {
        CleanupPreviewButton.IsEnabled = !_cleanupBusy;
        CleanupOverviewButton.IsEnabled = !_cleanupBusy;
        CleanupCancelButton.IsEnabled = _cleanupBusy;
        CleanupLocation.IsEnabled = !_cleanupBusy;
        CleanupFiles.IsHitTestVisible = !_cleanupBusy;
        KeyboardNavigation.SetTabNavigation(CleanupFiles,
            _cleanupBusy ? KeyboardNavigationMode.None : KeyboardNavigationMode.Continue);
        CleanupRecycleButton.IsEnabled = !_cleanupBusy && CleanupFiles.SelectedItems.Count > 0;
        CleanupRecycleAllButton.IsEnabled = !_cleanupBusy && _cleanupInventory is { Count: > 0 };
        CleanupSelectAllButton.IsEnabled = !_cleanupBusy && CleanupFiles.Items.Count > 0;
        UpdateCleanupPaging();
        if (_cleanupBusy) return;
        var count = CleanupFiles.SelectedItems.Count;
        if (count > 0)
        {
            long bytes = 0;
            foreach (var item in CleanupFiles.SelectedItems)
                if (item is UserTempCleanup.Candidate candidate)
                    bytes = bytes > long.MaxValue - candidate.Bytes ? long.MaxValue : bytes + candidate.Bytes;
            var label = count == 1 ? "fichier sélectionné" : "fichiers sélectionnés";
            CleanupStatus.Text = $"{count:N0} {label} · {FormatCleanupBytes(bytes)} (taille logique).";
        }
    }

    private async void PreviewCleanupOverview(object sender, RoutedEventArgs e)
    {
        if (_cleanupBusy) return;
        var token = StartCleanup();
        CleanupOverviewStatus.Text = "Évaluation en cours…";
        CleanupCategories.ItemsSource = null;
        try
        {
            var userProgress = CleanupProgress<UserTempCleanup.Progress>(p =>
                CleanupOverviewStatus.Text = $"Utilisateur : {p.Visited:N0} entrées examinées, {p.Eligible:N0} fichiers éligibles…");
            var user = await Task.Run(() => UserTempCleanup.Summarize(
                UserTempCleanup.Location.User, token, userProgress));
            token.ThrowIfCancellationRequested();
            var windowsProgress = CleanupProgress<UserTempCleanup.Progress>(p =>
                CleanupOverviewStatus.Text = $"Windows : {p.Visited:N0} entrées examinées, {p.Eligible:N0} fichiers éligibles…");
            var windows = await Task.Run(() => UserTempCleanup.Summarize(
                UserTempCleanup.Location.Windows, token, windowsProgress));
            _cleanupProgressVersion++;
            token.ThrowIfCancellationRequested();
            CleanupCategories.ItemsSource = new[]
            {
                new CleanupCategoryRow("Temporaires utilisateur", user.Accessible ? user.Eligible : null,
                    user.Accessible ? FormatCleanupBytes(user.LogicalBytes) : "Indisponible", UserTempCleanup.Location.User),
                new CleanupCategoryRow("Temporaires Windows", windows.Accessible ? windows.Eligible : null,
                    windows.Accessible ? FormatCleanupBytes(windows.LogicalBytes) : "Indisponible", UserTempCleanup.Location.Windows)
            };
            CleanupOverviewStatus.Text = "Sélectionnez une catégorie ci-dessous pour consulter ses fichiers.";
        }
        catch (OperationCanceledException)
        {
            CleanupOverviewStatus.Text = "Évaluation annulée.";
        }
        catch (Exception ex)
        {
            CleanupOverviewStatus.Text = "Évaluation impossible.";
            ShowCleanupMessage(ex.Message);
        }
        finally
        {
            FinishCleanup();
        }
    }

    private void SelectAllCleanupFiles(object sender, RoutedEventArgs e)
    {
        if (!_cleanupBusy && CleanupFiles.Items.Count > 0)
            CleanupFiles.SelectAll();
    }

    private async void PreviewUserTempCleanup(object sender, RoutedEventArgs e)
    {
        if (_cleanupBusy) return;
        var token = StartCleanup();
        ReleaseCleanupInventory();
        CleanupStatus.Text = "Recherche en cours…";
        try
        {
            var location = SelectedCleanupLocation;
            var progress = CleanupProgress<UserTempCleanup.Progress>(p =>
                CleanupStatus.Text = $"{p.Visited:N0} entrées examinées · {p.Eligible:N0} fichiers · {FormatCleanupBytes(p.LogicalBytes)}…");
            var inventory = await Task.Run(() => UserTempCleanup.CreateInventory(location, token, progress));
            _cleanupProgressVersion++;
            _cleanupInventory = inventory;
            token.ThrowIfCancellationRequested();
            ShowCleanupPage(0);
            ShowCleanupInventoryStatus();
        }
        catch (OperationCanceledException)
        {
            ReleaseCleanupInventory();
            CleanupStatus.Text = "Recherche annulée.";
        }
        catch (Exception ex)
        {
            ReleaseCleanupInventory();
            CleanupStatus.Text = "Recherche impossible.";
            ShowCleanupMessage(ex.Message);
        }
        finally { FinishCleanup(); }
    }

    private async void RecycleAllCleanup(object sender, RoutedEventArgs e)
    {
        if (_cleanupBusy || _cleanupInventory is not { Count: > 0 } inventory) return;
        var location = SelectedCleanupLocation;
        if (MessageBox.Show(this,
            $"Déplacer TOUS les {inventory.Count:N0} fichiers indexés ({FormatCleanupBytes(inventory.LogicalBytes)} logiques) vers la Corbeille ?\n\nLes fichiers sont revérifiés individuellement. Cette opération peut être longue et l’espace libre n’augmentera pas avant de vider la Corbeille.",
            "Confirmer le nettoyage complet", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await RunCleanupAsync((token, progress) => UserTempCleanup.RecycleInventory(inventory, location, token, progress),
            "Nettoyage par lots en cours…", " · Nettoyage terminé.");
    }

    private async void RecycleUserTempCleanup(object sender, RoutedEventArgs e)
    {
        if (_cleanupBusy) return;
        var selected = CleanupFiles.SelectedItems.Cast<UserTempCleanup.Candidate>().ToArray();
        if (selected.Length == 0) return;
        long total = 0;
        foreach (var file in selected)
            total = total > long.MaxValue - file.Bytes ? long.MaxValue : total + file.Bytes;
        var fileLabel = selected.Length == 1 ? "fichier" : "fichiers";
        if (MessageBox.Show(this,
            $"Déplacer {selected.Length:N0} {fileLabel} ({FormatCleanupBytes(total)} logiques) vers la Corbeille ?\n\nL’espace libre ne sera pas nécessairement augmenté avant de vider la Corbeille.",
            "Confirmer le nettoyage", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var location = SelectedCleanupLocation;
        await RunCleanupAsync((token, progress) => UserTempCleanup.RecycleWithReport(selected, location, token, progress),
            "Déplacement vers la Corbeille…", " · Résultats du disque à actualiser.");
    }

    private async Task RunCleanupAsync(
        Func<CancellationToken, IProgress<UserTempCleanup.RecycleProgress>, UserTempCleanup.RecycleReport> recycle,
        string runningStatus, string completedStatus)
    {
        var token = StartCleanup();
        CleanupStatus.Text = runningStatus;
        try
        {
            var progress = CleanupProgress<UserTempCleanup.RecycleProgress>(p =>
            {
                CleanupProgressBar.Maximum = Math.Max(1, p.Total);
                CleanupProgressBar.Value = p.Processed;
                CleanupProgressBar.IsIndeterminate = false;
                CleanupStatus.Text = $"{p.Processed:N0} / {p.Total:N0} traités · {p.Recycled:N0} déplacés · {p.Skipped:N0} ignorés…";
            });
            var report = await Task.Run(() => recycle(token, progress));
            _cleanupProgressVersion++;
            ReleaseCleanupInventory();
            var result = report.Result;
            CleanupStatus.Text = $"{result.Recycled:N0} déplacés · {result.Skipped:N0} ignorés · {FormatCleanupBytes(result.LogicalBytes)} logiques" +
                CleanupOutcome(report, completedStatus);
            if (!_closeAfterCleanup && report.Errors.Count > 0)
                MessageBox.Show(this, string.Join(Environment.NewLine, report.Errors.Take(20)) +
                    (report.Errors.Count > 20 ? "\n… (extrait des 100 premières anomalies)." : ""),
                    "Fichiers ignorés — détails", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ReleaseCleanupInventory();
            CleanupStatus.Text = "Nettoyage interrompu ; certains fichiers ont peut-être été déplacés.";
            ShowCleanupMessage(ex.Message);
        }
        finally
        {
            FinishCleanup();
        }
    }

    private static string CleanupOutcome(UserTempCleanup.RecycleReport report, string completed) =>
        report.Failure is { } failure ? $" · Inventaire interrompu : {failure}"
        : report.Cancelled ? " · Annulé : les déplacements déjà effectués sont conservés." : completed;

    private static string FormatCleanupBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes:N0} o";
        string[] units = ["Ko", "Mo", "Go", "To", "Po"];
        double value = bytes;
        int unit = -1;
        do { value /= 1024; unit++; } while (value >= 1024 && unit < units.Length - 1);
        return $"{value:N2} {units[unit]}";
    }

    private MainViewModel Model => (MainViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel(new DiskScanner());
        Model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.CurrentDirectory))
                foreach (var column in ExplorerTable.Columns) column.SortDirection = null;
        };
    }

    private void SelectExplorerDirectory(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is ExplorerNode { Directory: { } directory })
            Navigate(directory);
    }

    private void Navigate(ScanDirectory directory)
    {
        if (Model.NavigateMapCommand.CanExecute(directory))
            Model.NavigateMapCommand.Execute(directory);
    }

    private async void SortExplorer(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var direction = e.Column.SortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending : ListSortDirection.Ascending;
        if (await Model.SortExplorerAsync(e.Column.SortMemberPath, direction))
        {
            foreach (var column in ExplorerTable.Columns) column.SortDirection = null;
            e.Column.SortDirection = direction;
        }
    }

    private void OpenExplorerDirectory(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(ExplorerTable, source) is DataGridRow { Item: ExplorerRow { Directory: { } directory } })
            Navigate(directory);
    }

    private void HandleExplorerKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ExplorerTable.SelectedItem is ExplorerRow { Directory: { } directory })
            Navigate(directory);
        else if (e.Key == Key.Back && Model.NavigateBackCommand.CanExecute(null))
            Model.NavigateBackCommand.Execute(null);
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            CopyExplorerPath(sender, e);
        else
            return;
        e.Handled = true;
    }

    private void CopyExplorerPath(object sender, RoutedEventArgs e)
    {
        var path = (ExplorerTable.SelectedItem as ExplorerRow)?.FullPath ?? Model.CurrentDirectory?.Path;
        if (path is null) return;
        try
        {
            Clipboard.SetText(path);
        }
        catch (ExternalException)
        {
            MessageBox.Show(this, "Le presse-papiers est indisponible.", "Discoscope");
        }
    }

    private void OpenExplorerLocation(object sender, RoutedEventArgs e)
    {
        var row = ExplorerTable.SelectedItem as ExplorerRow;
        var path = row?.Directory?.Path ?? row?.Parent.Path ?? Model.CurrentDirectory?.Path;
        if (path is null) return;
        if (!Directory.Exists(path))
        {
            MessageBox.Show(this, "Ce dossier n’est plus accessible.", "Discoscope");
            return;
        }
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                UseShellExecute = false,
                ArgumentList = { path }
            });
        }
        catch (Win32Exception)
        {
            MessageBox.Show(this, "Impossible d’ouvrir l’Explorateur de fichiers.", "Discoscope");
        }
    }
}
