using System.Windows;
using Discoscope.Core.Services;
using Discoscope.Desktop.ViewModels;

namespace Discoscope.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel(new DiskScanner());
    }
}
