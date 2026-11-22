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
    private CancellationTokenSource? _allocationCancellation;
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
        if (!e.Cancel)
        {
            ResetAllocatedSpace();
            ReleaseCleanupInventory();
        }
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
        _cleanupProgressVersion++;
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
            $"Déplacer vers la Corbeille les {inventory.Count:N0} fichiers indexés sur toutes les pages ({FormatCleanupBytes(inventory.LogicalBytes)} de taille logique) ?\n\nEmplacement : {inventory.Root}\n\nChaque fichier est revérifié avant le déplacement. La taille indiquée ne garantit pas un gain d’espace libre.",
            "Confirmer le nettoyage complet", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await RunCleanupAsync((token, progress) => UserTempCleanup.RecycleInventory(inventory, location, token, progress),
            location, "Nettoyage par lots en cours…", " · Nettoyage terminé.");
    }

    private async void RecycleUserTempCleanup(object sender, RoutedEventArgs e)
    {
        if (_cleanupBusy) return;
        if (_cleanupInventory is not { } inventory) return;
        var selected = CleanupFiles.SelectedItems.Cast<UserTempCleanup.Candidate>().ToArray();
        if (selected.Length == 0) return;
        long total = 0;
        foreach (var file in selected)
            total = total > long.MaxValue - file.Bytes ? long.MaxValue : total + file.Bytes;
        var fileLabel = selected.Length == 1 ? "fichier" : "fichiers";
        var location = SelectedCleanupLocation;
        if (MessageBox.Show(this,
            $"Déplacer vers la Corbeille {selected.Length:N0} {fileLabel} de la page actuelle ({FormatCleanupBytes(total)} de taille logique) ?\n\nEmplacement : {inventory.Root}\n\nChaque fichier est revérifié avant le déplacement. La taille indiquée ne garantit pas un gain d’espace libre.",
            "Confirmer le nettoyage", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await RunCleanupAsync((token, progress) => UserTempCleanup.RecycleWithReport(selected, location, token, progress),
            location, "Déplacement vers la Corbeille…", " · Résultats du disque à actualiser.");
    }

    private async Task RunCleanupAsync(
        Func<CancellationToken, IProgress<UserTempCleanup.RecycleProgress>, UserTempCleanup.RecycleReport> recycle,
        UserTempCleanup.Location location, string runningStatus, string completedStatus)
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
            var (report, before, after) = await Task.Run(() =>
            {
                var root = UserTempCleanup.GetRoot(location);
                var freeBefore = TryGetFreeBytes(root);
                var result = recycle(token, progress);
                return (result, freeBefore, TryGetFreeBytes(root));
            });
            _cleanupProgressVersion++;
            ReleaseCleanupInventory();
            var result = report.Result;
            CleanupStatus.Text = $"{report.Processed:N0} / {report.Total:N0} traités · " +
                $"{result.Recycled:N0} déplacés · {result.Skipped:N0} ignorés" +
                (report.Unprocessed > 0 ? $" · {report.Unprocessed:N0} non traités" : "") +
                $" · {FormatCleanupBytes(result.LogicalBytes)} déplacés (taille logique)" +
                CleanupOutcome(report, completedStatus) +
                (before is { } previous && after is { } current
                    ? $" · Libre sur le volume : {SizeFormatter.Format(previous)} → {SizeFormatter.Format(current)}."
                    : "");
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

    private static long? TryGetFreeBytes(string path)
    {
        try { return new VolumeInspector().Inspect(path).FreeBytes; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or System.Security.SecurityException) { return null; }
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
            {
                ResetAllocatedSpace();
                ResetExplorerFilters();
            }
        };
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

    private async void OpenExplorerWorkspace(object sender, RoutedEventArgs e)
    {
        if (FileTypesTable.SelectedItem is FileTypeRow { AgeBand: { } band })
        {
            await OpenAgeBandAsync(band);
            return;
        }
        WorkspaceTabs.SelectedItem = ExplorerWorkspace;
        ExplorerWorkspace.Focus();
    }

    private void OpenWindowsStorageSettings(object sender, RoutedEventArgs e)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("ms-settings:storagesense")
                { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            MessageBox.Show(this, "Impossible d’ouvrir les paramètres de stockage Windows.", "Discoscope");
        }
    }

    private void ScrollExplorerPath(object sender, ScrollChangedEventArgs e)
    {
        if (ReferenceEquals(sender, e.OriginalSource) && e.ExtentWidthChange != 0 && sender is ScrollViewer path)
            path.ScrollToRightEnd();
    }

    private void Navigate(ScanDirectory directory)
    {
        if (Model.NavigateMapCommand.CanExecute(directory))
            Model.NavigateMapCommand.Execute(directory);
    }

    private sealed record AllocatedFileRow(string Path, long AllocatedBytes, long LogicalBytes)
    {
        public string AllocatedLabel => SizeFormatter.Format(AllocatedBytes);
        public string LogicalLabel => SizeFormatter.Format(LogicalBytes);
    }

    private void ToggleFileRanking(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, LargestLogicalFiles))
            LargestAllocatedFiles.IsChecked = false;
        else
            LargestLogicalFiles.IsChecked = false;
    }

    private void ResetAllocatedSpace()
    {
        _allocationCancellation?.Cancel();
        _allocationCancellation = null;
        AllocatedSpaceButton.Content = "Mesurer l’espace alloué";
        AllocatedSpaceButton.IsEnabled = true;
        AllocatedSpaceStatus.Text = "";
        LargestAllocatedFilesTable.ItemsSource = null;
        LargestAllocatedFiles.IsChecked = false;
        LargestAllocatedFiles.Visibility = Visibility.Collapsed;
    }

    private async void MeasureAllocatedSpace(object sender, RoutedEventArgs e)
    {
        if (_allocationCancellation is { } active)
        {
            active.Cancel();
            AllocatedSpaceButton.IsEnabled = false;
            AllocatedSpaceStatus.Text = "Annulation de la mesure…";
            return;
        }
        if (Model.CurrentDirectory is not { } directory) return;

        using var cancellation = new CancellationTokenSource();
        _allocationCancellation = cancellation;
        LargestAllocatedFilesTable.ItemsSource = null;
        LargestAllocatedFiles.IsChecked = false;
        LargestAllocatedFiles.Visibility = Visibility.Collapsed;
        AllocatedSpaceButton.Content = "Annuler la mesure";
        AllocatedSpaceStatus.Text = "Mesure des fichiers indexés en cours…";
        var progress = new Progress<AllocatedSpaceProgress>(update =>
        {
            if (!ReferenceEquals(_allocationCancellation, cancellation) || cancellation.IsCancellationRequested)
                return;
            AllocatedSpaceStatus.Text = $"{update.ProcessedPaths:N0} / {update.TotalPaths:N0} chemins vérifiés · " +
                                        $"{SizeFormatter.Format(update.AllocatedBytes)} alloués";
        });
        string status;
        try
        {
            var result = await new AllocatedSpaceInspector().InspectAsync(directory, progress, cancellation.Token);
            if (!ReferenceEquals(_allocationCancellation, cancellation)) return;
            cancellation.Token.ThrowIfCancellationRequested();
            if (result.LargestFiles.Count > 0)
            {
                LargestAllocatedFilesTable.ItemsSource = result.LargestFiles.Select(file =>
                    new AllocatedFileRow(file.Path, file.AllocatedBytes, file.LogicalBytes)).ToArray();
                LargestAllocatedFiles.Visibility = Visibility.Visible;
            }
            status = result.MeasuredPaths == 0 && result.UnavailablePaths > 0
                ? $"Mesure indisponible · {result.UnavailablePaths:N0} chemin(s) non mesuré(s)"
                : $"{SizeFormatter.Format(result.Bytes)} alloués / {SizeFormatter.Format(result.MeasuredLogicalBytes)} logiques " +
                  $"pour {result.MeasuredPaths:N0} fichier(s) mesuré(s) · " +
                  $"{result.DuplicateLinks:N0} lien(s) en double · {result.UnavailablePaths:N0} chemin(s) non mesuré(s)";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            status = "Mesure annulée.";
        }
        catch (PlatformNotSupportedException)
        {
            status = "Mesure disponible sur les volumes NTFS locaux.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException)
        {
            status = "Mesure indisponible.";
        }

        if (!ReferenceEquals(_allocationCancellation, cancellation)) return;
        _allocationCancellation = null;
        AllocatedSpaceButton.Content = "Mesurer l’espace alloué";
        AllocatedSpaceButton.IsEnabled = true;
        AllocatedSpaceStatus.Text = status;
    }

    private void ResetExplorerFilters()
    {
        ExplorerNameFilter.Clear();
        ExplorerExtensionFilter.Clear();
        ExplorerSizeFilter.Clear();
        ExplorerAgeFilter.SelectedIndex = 0;
        ExplorerVolumeScope.IsChecked = false;
        ExplorerFilterStatus.Text = "";
        foreach (var column in ExplorerTable.Columns) column.SortDirection = null;
    }

    private async void ClearExplorerFilter(object sender, RoutedEventArgs e)
    {
        ResetExplorerFilters();
        await FilterExplorerAsync();
    }

    private async void ApplyExplorerFilter(object sender, RoutedEventArgs e) => await FilterExplorerAsync();

    private async void ExplorerFilterKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await FilterExplorerAsync();
    }

    private async void OpenAgeDistributionRow(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(FileTypesTable, source) is DataGridRow
            { Item: FileTypeRow { AgeBand: { } band } })
            await OpenAgeBandAsync(band);
    }

    private async void OpenAgeDistributionRowWithKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || FileTypesTable.SelectedItem is not FileTypeRow { AgeBand: { } band }) return;
        e.Handled = true;
        await OpenAgeBandAsync(band);
    }

    private async Task OpenAgeBandAsync(FileAgeBand ageBand)
    {
        ResetExplorerFilters();
        ExplorerAgeFilter.SelectedValue = ageBand;
        ExplorerVolumeScope.IsChecked = true;
        WorkspaceTabs.SelectedItem = ExplorerWorkspace;
        await FilterExplorerAsync();
    }

    private async Task FilterExplorerAsync()
    {
        const long bytesPerMib = 1024 * 1024;
        string size = ExplorerSizeFilter.Text.Trim();
        long minimumMib = 0;
        if (size.Length > 0 && (!long.TryParse(size, out minimumMib) ||
            minimumMib < 0 || minimumMib > long.MaxValue / bytesPerMib))
        {
            ExplorerFilterStatus.Text = "Saisissez une taille entière positive en Mio.";
            return;
        }

        string extension = ExplorerExtensionFilter.Text.Trim().TrimStart('.');
        var ageBand = ExplorerAgeFilter.SelectedValue is FileAgeBand band ? band : (FileAgeBand?)null;
        DateTime? cutoff = ReferenceEquals(ExplorerAgeFilter.SelectedItem, OlderThanThirtyDaysFilter)
            ? (Model.FileAgeReferenceUtc ?? DateTime.UtcNow).AddDays(-30) : null;
        string name = ExplorerNameFilter.Text.Trim();
        bool wholeVolume = ExplorerVolumeScope.IsChecked == true;
        if (wholeVolume && name.Length == 0 && extension.Length == 0 && minimumMib == 0 && cutoff is null && ageBand is null)
        {
            ExplorerFilterStatus.Text = "Ajoutez un critère pour rechercher sur le lecteur.";
            return;
        }

        if (await Model.FilterExplorerAsync(name, extension.Length == 0 ? "" : "." + extension,
            minimumMib * bytesPerMib, cutoff, wholeVolume, ageBand) && Model.ExplorerItems is { } results)
        {
            foreach (var column in ExplorerTable.Columns) column.SortDirection = null;
            ExplorerFilterStatus.Text = results.TotalMatches > results.Count
                ? $"{results.Count:N0} affichés sur {results.TotalMatches:N0}"
                : $"{results.TotalMatches:N0} résultat(s)";
        }
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
        bool selectFile = row is { Directory: null };
        var path = row?.FullPath ?? Model.CurrentDirectory?.Path;
        if (path is null) return;
        if (selectFile ? !File.Exists(path) : !Directory.Exists(path))
        {
            MessageBox.Show(this, "Cet emplacement n’est plus accessible.", "Discoscope");
            return;
        }
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                UseShellExecute = false
            };
            if (selectFile)
                start.Arguments = $"/select,\"{path}\"";
            else
                start.ArgumentList.Add(path);
            using var process = Process.Start(start);
        }
        catch (Win32Exception)
        {
            MessageBox.Show(this, "Impossible d’ouvrir l’Explorateur de fichiers.", "Discoscope");
        }
    }
}
