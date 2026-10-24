using System.Windows;
using Daybreak.Launcher.ViewModels;
using Wpf.Ui.Controls;

namespace Daybreak.Launcher;

public partial class MainWindow : FluentWindow
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        Loaded += (_, _) => RootNavigation.Navigate(typeof(Views.Pages.HomePage));
    }
}
