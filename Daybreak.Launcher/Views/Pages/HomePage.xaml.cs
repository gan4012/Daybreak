using System.Windows.Controls;
using Daybreak.Launcher.ViewModels;

namespace Daybreak.Launcher.Views.Pages;

public partial class HomePage : Page
{
    public HomePage()
    {
        InitializeComponent();
        DataContext = App.GetService<HomeViewModel>();
    }

    private void OnLogTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox box)
            box.ScrollToEnd();
    }
}
