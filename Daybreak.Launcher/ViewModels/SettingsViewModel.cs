using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Daybreak.Core.Game;
using Daybreak.Core.Storage;
using Serilog;

namespace Daybreak.Launcher.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly LauncherConfig _config = LauncherConfig.Load();

    [ObservableProperty]
    private bool _isDarkTheme;

    [ObservableProperty]
    private string _gameDirectory;

    [ObservableProperty]
    private string _javaDirectory;

    [ObservableProperty]
    private int _maxMemoryMb;

    [ObservableProperty]
    private int _selectedMirrorIndex;

    [ObservableProperty]
    private bool _versionIsolation;

    [ObservableProperty]
    private string _settingsStatusText = "";

    public LauncherConfig Config => _config;

    public SettingsViewModel()
    {
        _isDarkTheme = _config.ThemeMode != "Light";
        _gameDirectory = _config.GameDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
        _javaDirectory = _config.JavaDirectory ?? "(自动探测)";
        _maxMemoryMb = _config.MaxMemoryMb;
        _selectedMirrorIndex = _config.Mirror == "bmclapi" ? 1 : 0;
        _versionIsolation = _config.VersionIsolation;
    }

    partial void OnIsDarkThemeChanged(bool value)
    {
        var theme = value ? "Dark" : "Light";
        Services.ThemeService.ApplyThemeMode(theme, _config.Backdrop);
        _config.ThemeMode = theme;
        _config.Save();
        Log.Information("主题切换为 {Theme}", theme);
    }

    [RelayCommand]
    private void SaveGameDirectory()
    {
        var dir = GameDirectory.Trim();
        if (string.IsNullOrEmpty(dir))
        {
            SettingsStatusText = "游戏目录不能为空";
            return;
        }

        _config.GameDirectory = dir;
        _config.Save();
        SettingsStatusText = "游戏目录已保存(重启启动器后版本列表按新目录刷新)";
        Log.Information("游戏目录更改为 {Directory}", dir);
    }

    [RelayCommand]
    private void SaveJavaDirectory()
    {
        var dir = JavaDirectory.Trim();
        if (dir.Length == 0 || dir == "(自动探测)")
        {
            _config.JavaDirectory = null;
            _config.Save();
            SettingsStatusText = "已清除 Java 路径,启动时自动探测/下载";
            Log.Information("Java 路径已清除");
            return;
        }

        var javaExe = Path.Combine(dir, "bin", "java.exe");
        if (!File.Exists(javaExe))
        {
            SettingsStatusText = $"未找到 {javaExe},请填写 Java 的安装根目录";
            return;
        }

        _config.JavaDirectory = dir;
        _config.Save();
        SettingsStatusText = "Java 路径已保存";
        Log.Information("Java 路径更改为 {Directory}", dir);
    }

    [RelayCommand]
    private void SaveMemory()
    {
        if (MaxMemoryMb is < 1024 or > 32_768)
        {
            SettingsStatusText = "内存需在 1024 ~ 32768 MB 之间";
            return;
        }

        _config.MaxMemoryMb = MaxMemoryMb;
        _config.Save();
        SettingsStatusText = $"内存分配已保存为 {MaxMemoryMb} MB";
    }

    partial void OnSelectedMirrorIndexChanged(int value)
    {
        _config.Mirror = value == 1 ? "bmclapi" : "official";
        _config.Save();
        Log.Information("下载镜像切换为 {Mirror}", _config.Mirror);
    }

    partial void OnVersionIsolationChanged(bool value)
    {
        _config.VersionIsolation = value;
        _config.Save();
        Log.Information("版本隔离切换为 {Value}", value);
    }
}
