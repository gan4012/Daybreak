using System.Windows.Controls;
using Daybreak.Launcher.ViewModels;

namespace Daybreak.Launcher.Views.Pages;

public partial class DownloadPage : Page
{
    public DownloadPage()
    {
        InitializeComponent();
        DataContext = App.GetService<DownloadViewModel>();
    }
}
