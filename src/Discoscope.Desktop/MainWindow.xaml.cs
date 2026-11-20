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
    private async void PreviewUserTempCleanup(object sender, RoutedEventArgs e)
    {
        if (_cleanupBusy) return;
        _cleanupBusy = true;
        CleanupFiles.ItemsSource = null;
        CleanupStatus.Text = "Recherche en cours…";
        try
        {
            var candidates = await Task.Run(() => Discoscope.Desktop.Services.UserTempCleanup.Preview());
            CleanupFiles.ItemsSource = candidates;
            CleanupStatus.Text = $"{candidates.Count:N0} fichiers trouvés (maximum 500).";
        }
        catch (Exception ex)
        {
            CleanupStatus.Text = "Recherche impossible.";
            MessageBox.Show(this, ex.Message, "Discoscope");
        }
        finally { _cleanupBusy = false; }
    }

    private async void RecycleUserTempCleanup(object sender, RoutedEventArgs e)
    {
        if (_cleanupBusy) return;
        var selected = CleanupFiles.SelectedItems.Cast<Discoscope.Desktop.Services.UserTempCleanup.Candidate>().ToArray();
        if (selected.Length == 0) return;
        var total = selected.Sum(file => file.Bytes);
        if (MessageBox.Show(this,
            $"Déplacer {selected.Length:N0} fichiers ({total:N0} octets logiques) vers la Corbeille ?",
            "Confirmer le nettoyage", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _cleanupBusy = true;
        CleanupStatus.Text = "Déplacement vers la Corbeille…";
        try
        {
            var result = await Task.Run(() => Discoscope.Desktop.Services.UserTempCleanup.Recycle(selected));
            CleanupFiles.ItemsSource = null;
            CleanupStatus.Text = $"{result.Recycled:N0} fichiers déplacés · {result.Skipped:N0} ignorés. Relancez l’analyse.";
        }
        catch (Exception ex)
        {
            CleanupStatus.Text = "Nettoyage interrompu.";
            MessageBox.Show(this, ex.Message, "Discoscope");
        }
        finally { _cleanupBusy = false; }
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
