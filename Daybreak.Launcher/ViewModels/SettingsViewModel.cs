using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Daybreak.Core.Storage;
using Daybreak.Launcher.Services;
using Serilog;

namespace Daybreak.Launcher.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    public LauncherConfig Config { get; } = LauncherConfig.Load();

    [ObservableProperty]
    private string _selectedThemeMode;

    [ObservableProperty]
    private string _selectedBackdrop;

    public SettingsViewModel()
    {
        _selectedThemeMode = Config.ThemeMode;
        _selectedBackdrop = Config.Backdrop;
    }

    [RelayCommand]
    private void SetThemeMode(string mode)
    {
        if (string.IsNullOrEmpty(mode))
            return;

        SelectedThemeMode = mode;
        Config.ThemeMode = mode;
        ThemeService.ApplyThemeMode(mode, Config.Backdrop);
        Config.Save();
        Log.Information("主题模式切换为 {ThemeMode}", mode);
    }

    [RelayCommand]
    private void SetBackdrop(string backdrop)
    {
        if (string.IsNullOrEmpty(backdrop))
            return;

        SelectedBackdrop = backdrop;
        Config.Backdrop = backdrop;
        if (Application.Current?.MainWindow is Window window)
            ThemeService.ApplyBackdrop(window, backdrop);
        Config.Save();
        Log.Information("背景效果切换为 {Backdrop}", backdrop);
    }
}
