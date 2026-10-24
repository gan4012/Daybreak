using System.Windows;
using System.Windows.Controls;
using Daybreak.Launcher.ViewModels;

namespace Daybreak.Launcher.Views.Pages;

public partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        DataContext = App.GetService<SettingsViewModel>();
        SyncRadioState();
    }

    private void SyncRadioState()
    {
        var vm = (SettingsViewModel)DataContext;
        ThemeLightRadio.IsChecked = vm.SelectedThemeMode == "Light";
        ThemeDarkRadio.IsChecked = vm.SelectedThemeMode == "Dark";
        ThemeSystemRadio.IsChecked = vm.SelectedThemeMode == "System";
        BackdropAcrylicRadio.IsChecked = vm.SelectedBackdrop == "Acrylic";
        BackdropMicaRadio.IsChecked = vm.SelectedBackdrop == "Mica";
        BackdropNoneRadio.IsChecked = vm.SelectedBackdrop == "None";
    }

    private void ThemeMode_Checked(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm || sender is not RadioButton { Tag: string mode })
            return;
        if (vm.SelectedThemeMode == mode)
            return; // 程序化设置 IsChecked 触发的回声,勿重复应用
        vm.SetThemeModeCommand.Execute(mode);
    }

    private void Backdrop_Checked(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm || sender is not RadioButton { Tag: string backdrop })
            return;
        if (vm.SelectedBackdrop == backdrop)
            return;
        vm.SetBackdropCommand.Execute(backdrop);
    }
}
