using System.Windows.Controls;
using Daybreak.Launcher.ViewModels;

namespace Daybreak.Launcher.Views.Pages;

public partial class VersionsPage : Page
{
    public VersionsPage()
    {
        InitializeComponent();
        DataContext = App.GetService<VersionsViewModel>();
    }
}
