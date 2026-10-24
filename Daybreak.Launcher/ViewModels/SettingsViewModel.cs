using CommunityToolkit.Mvvm.ComponentModel;
using Daybreak.Core.Storage;
using Serilog;
using Wpf.Ui.Appearance;

namespace Daybreak.Launcher.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isDarkTheme;

    [ObservableProperty]
    private LauncherConfig _config = LauncherConfig.Load();

    partial void OnIsDarkThemeChanged(bool value)
    {
        var theme = value ? ApplicationTheme.Dark : ApplicationTheme.Light;
        ApplicationThemeManager.Apply(theme);
        Config.Theme = value ? "Dark" : "Light";
        Config.Save();
        Log.Information("主题切换为 {Theme}", Config.Theme);
    }

    public SettingsViewModel()
    {
        _isDarkTheme = Config.Theme != "Light";
    }
}
