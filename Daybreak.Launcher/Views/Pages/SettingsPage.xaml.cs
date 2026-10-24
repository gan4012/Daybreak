using Daybreak.Launcher.ViewModels;
using System.Windows.Controls;

namespace Daybreak.Launcher.Views.Pages;

public partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        DataContext = App.GetService<SettingsViewModel>();
    }
}
