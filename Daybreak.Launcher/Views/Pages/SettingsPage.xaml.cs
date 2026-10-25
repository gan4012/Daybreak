using System.Windows.Controls;
using Daybreak.Launcher.ViewModels;

namespace Daybreak.Launcher.Views.Pages;

public partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        DataContext = App.GetService<SettingsViewModel>();
    }
}
