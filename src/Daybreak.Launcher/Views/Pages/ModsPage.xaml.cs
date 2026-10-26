using System.Windows.Controls;
using System.Windows.Input;
using Daybreak.Launcher.ViewModels;

namespace Daybreak.Launcher.Views.Pages;

public partial class ModsPage : Page
{
    public ModsPage()
    {
        InitializeComponent();
        DataContext = App.GetService<ModsViewModel>();
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is ModsViewModel viewModel)
            viewModel.SearchCommand.Execute(null);
    }
}
