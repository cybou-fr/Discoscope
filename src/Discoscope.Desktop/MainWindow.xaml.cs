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

namespace Discoscope.Desktop;

public partial class MainWindow : Window
{
    private bool _cleanupBusy;
    private sealed record CleanupCategoryRow(string Name, int Count, string Size, Discoscope.Desktop.Services.UserTempCleanup.Location Location);

    private void CleanupCategorySelected(object sender, SelectionChangedEventArgs e)
    {
        if (_cleanupBusy || CleanupCategories.SelectedItem is not CleanupCategoryRow row) return;
        CleanupLocation.SelectedIndex = row.Location == Discoscope.Desktop.Services.UserTempCleanup.Location.User ? 0 : 1;
        CleanupStatus.Text = "Catégorie choisie. Cliquez sur Rechercher pour voir les fichiers.";
    }
    private CancellationTokenSource? _cleanupCancellation;
    private Discoscope.Desktop.Services.UserTempCleanup.Location SelectedCleanupLocation =>
        CleanupLocation.SelectedIndex == 1
            ? Discoscope.Desktop.Services.UserTempCleanup.Location.Windows
            : Discoscope.Desktop.Services.UserTempCleanup.Location.User;

    private void CleanupLocationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CleanupFiles is null || CleanupStatus is null) return;
        CleanupFiles.ItemsSource = null;
        CleanupStatus.Text = "Recherchez les fichiers de cet emplacement.";
        if (CleanupPreviewButton is not null && CleanupRecycleButton is not null && CleanupSelectAllButton is not null && CleanupOverviewButton is not null && CleanupCancelButton is not null)
            UpdateCleanupSelection();
    }


    private void CleanupSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CleanupPreviewButton is not null && CleanupFiles is not null)
            UpdateCleanupSelection();
    }

    private void CancelCleanup(object sender, RoutedEventArgs e) => _cleanupCancellation?.Cancel();

    private void UpdateCleanupSelection()
    {
        CleanupPreviewButton.IsEnabled = !_cleanupBusy;
        CleanupOverviewButton.IsEnabled = !_cleanupBusy;
        CleanupCancelButton.IsEnabled = _cleanupBusy;
        CleanupLocation.IsEnabled = !_cleanupBusy;
        CleanupRecycleButton.IsEnabled = !_cleanupBusy && CleanupFiles.SelectedItems.Count > 0;
        CleanupSelectAllButton.IsEnabled = !_cleanupBusy && CleanupFiles.Items.Count > 0;
        if (_cleanupBusy) return;
        var count = CleanupFiles.SelectedItems.Count;
        if (count > 0)
        {
            long bytes = 0;
            foreach (var item in CleanupFiles.SelectedItems)
                if (item is Discoscope.Desktop.Services.UserTempCleanup.Candidate candidate)
                    bytes = bytes > long.MaxValue - candidate.Bytes ? long.MaxValue : bytes + candidate.Bytes;
            CleanupStatus.Text = $"{count:N0} sélectionnés · {FormatCleanupBytes(bytes)} (taille logique).";
        }
    }

    private async void PreviewCleanupOverview(object sender, RoutedEventArgs e)
    {
        if (_cleanupBusy) return;
        _cleanupBusy = true;
        _cleanupCancellation = new CancellationTokenSource();
        UpdateCleanupSelection();
        CleanupOverviewStatus.Text = "Évaluation en cours…";
        CleanupCategories.ItemsSource = null;
        try
        {
            var token = _cleanupCancellation.Token;
            var user = await Task.Run(() => Discoscope.Desktop.Services.UserTempCleanup.Preview(
                Discoscope.Desktop.Services.UserTempCleanup.Location.User, token));
            var windows = await Task.Run(() => Discoscope.Desktop.Services.UserTempCleanup.Preview(
                Discoscope.Desktop.Services.UserTempCleanup.Location.Windows, token));
            static long Total(IReadOnlyList<Discoscope.Desktop.Services.UserTempCleanup.Candidate> items)
            {
                long bytes = 0;
                foreach (var item in items)
                    bytes = bytes > long.MaxValue - item.Bytes ? long.MaxValue : bytes + item.Bytes;
                return bytes;
            }
            CleanupCategories.ItemsSource = new[]
            {
                new CleanupCategoryRow("Temporaires utilisateur", user.Count, FormatCleanupBytes(Total(user)), Discoscope.Desktop.Services.UserTempCleanup.Location.User),
                new CleanupCategoryRow("Temporaires Windows", windows.Count, FormatCleanupBytes(Total(windows)), Discoscope.Desktop.Services.UserTempCleanup.Location.Windows)
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
            MessageBox.Show(this, ex.Message, "Discoscope");
        }
        finally
        {
            _cleanupCancellation?.Dispose();
            _cleanupCancellation = null;
            _cleanupBusy = false;
            UpdateCleanupSelection();
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
        _cleanupBusy = true;
        _cleanupCancellation = new CancellationTokenSource();
        UpdateCleanupSelection();
        CleanupFiles.ItemsSource = null;
        CleanupStatus.Text = "Recherche en cours…";
        try
        {
            var location = SelectedCleanupLocation;
            var token = _cleanupCancellation!.Token;
            var candidates = await Task.Run(() => Discoscope.Desktop.Services.UserTempCleanup.Preview(location, token));
            CleanupFiles.ItemsSource = candidates;
            long total = 0;
            foreach (var candidate in candidates)
                total = total > long.MaxValue - candidate.Bytes ? long.MaxValue : total + candidate.Bytes;
            CleanupStatus.Text = $"{candidates.Count:N0} fichiers · {FormatCleanupBytes(total)} (taille logique).";
        }
        catch (OperationCanceledException)
        {
            CleanupStatus.Text = "Recherche annulée.";
        }
        catch (Exception ex)
        {
            CleanupStatus.Text = "Recherche impossible.";
            MessageBox.Show(this, ex.Message, "Discoscope");
        }
        finally { _cleanupCancellation?.Dispose(); _cleanupCancellation = null; _cleanupBusy = false; UpdateCleanupSelection(); }
    }

    private async void RecycleUserTempCleanup(object sender, RoutedEventArgs e)
    {
        if (_cleanupBusy) return;
        var selected = CleanupFiles.SelectedItems.Cast<Discoscope.Desktop.Services.UserTempCleanup.Candidate>().ToArray();
        if (selected.Length == 0) return;
        long total = 0;
        foreach (var file in selected)
            total = total > long.MaxValue - file.Bytes ? long.MaxValue : total + file.Bytes;
        if (MessageBox.Show(this,
            $"Déplacer {selected.Length:N0} fichiers ({FormatCleanupBytes(total)} logiques) vers la Corbeille ?\n\nL’espace libre ne sera pas nécessairement augmenté avant de vider la Corbeille.",
            "Confirmer le nettoyage", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var location = SelectedCleanupLocation;
        _cleanupBusy = true;
        _cleanupCancellation = new CancellationTokenSource();
        UpdateCleanupSelection();
        CleanupStatus.Text = "Déplacement vers la Corbeille…";
        try
        {
            var token = _cleanupCancellation!.Token;
            var result = await Task.Run(() => Discoscope.Desktop.Services.UserTempCleanup.Recycle(selected, location, token));
            var remaining = await Task.Run(() => Discoscope.Desktop.Services.UserTempCleanup.Preview(location, token));
            CleanupFiles.ItemsSource = remaining;
            CleanupStatus.Text = $"{result.Recycled:N0} fichiers déplacés ({FormatCleanupBytes(result.LogicalBytes)} logiques) · {result.Skipped:N0} ignorés. Résultats du disque à actualiser.";
        }
        catch (OperationCanceledException)
        {
            CleanupFiles.ItemsSource = null;
            CleanupStatus.Text = "Opération annulée. Certains fichiers peuvent déjà avoir été déplacés. Relancez la recherche.";
        }
        catch (Exception ex)
        {
            CleanupStatus.Text = "Nettoyage interrompu.";
            MessageBox.Show(this, ex.Message, "Discoscope");
        }
        finally { _cleanupCancellation?.Dispose(); _cleanupCancellation = null; _cleanupBusy = false; UpdateCleanupSelection(); }
    }

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
