using CommunityToolkit.Mvvm.ComponentModel;

namespace Daybreak.Launcher.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private string _appTitle = "Daybreak 破晓启动器";
}
