using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Daybreak.Core.Game;
using Daybreak.Core.Storage;
using Serilog;

namespace Daybreak.Launcher.ViewModels;

public partial class HomeViewModel : ObservableObject
{
    private const int MaxLogLines = 400;

    private readonly LauncherConfig _config = LauncherConfig.Load();
    private readonly StringBuilder _logBuffer = new();
    private LaunchService? _launchService;
    private Process? _gameProcess;

    public ObservableCollection<string> InstalledVersions { get; } = [];

    [ObservableProperty]
    private string? _selectedVersion;

    [ObservableProperty]
    private string _playerName;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "就绪";

    /// <summary>0~100;0 表示无法确定总量(显示不确定进度)。</summary>
    [ObservableProperty]
    private int _progressPercent;

    [ObservableProperty]
    private string _logText = "";

    public bool HasProgress => ProgressPercent > 0;

    public HomeViewModel()
    {
        PlayerName = _config.PlayerName;
        _ = LoadVersionsAsync();
    }

    partial void OnPlayerNameChanged(string value)
    {
        _config.PlayerName = value;
        _config.Save();
    }

    partial void OnIsBusyChanged(bool value)
    {
        LaunchCommand.NotifyCanExecuteChanged();
        OpenJavaDownloadSiteCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedVersionChanged(string? value) => LaunchCommand.NotifyCanExecuteChanged();

    private LaunchService GetService() =>
        _launchService ??= new LaunchService(_config.GameDirectory, _config.Mirror);

    [RelayCommand]
    private async Task LoadVersionsAsync()
    {
        try
        {
            var versions = await Task.Run(() => GetService().GetInstalledVersions());
            InstalledVersions.Clear();
            foreach (var version in versions)
                InstalledVersions.Add(version);

            SelectedVersion ??= versions.FirstOrDefault();
            StatusText = versions.Count == 0
                ? "还没有已安装的版本,去「下载游戏」页获取"
                : $"{versions.Count} 个已安装版本";
            Log($"版本列表刷新:{versions.Count} 个");
        }
        catch (Exception ex)
        {
            Log($"版本列表刷新失败:{ex.Message}");
            StatusText = "读取版本列表失败:" + ex.Message;
        }
    }

    private bool CanLaunch() => !IsBusy && SelectedVersion is not null;

    private bool CanDownloadJava() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanDownloadJava))]
    private void OpenJavaDownloadSite()
    {
        // Adoptium 官网的安装包最终托管在 GitHub Releases,被墙环境点下载会超时;
        // 清华 TUNA 镜像同步了全部 Temurin 构建,国内直连可达。
        // 默认打开 Java 21 JRE(覆盖 1.20.5+ 与 1.18+ 大多数版本);需要 17 时把路径里的 21 换成 17。
        const string url = "https://mirrors.tuna.tsinghua.edu.cn/Adoptium/21/jre/x64/windows/";
        const string url17 = "https://mirrors.tuna.tsinghua.edu.cn/Adoptium/17/jre/x64/windows/";
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            Log($"已打开 Java 下载页(清华镜像):{url}");
            Log($"旧版本(1.18~1.20.4)需要的 Java 17:{url17}");
            StatusText = "已打开 Java 下载页(清华镜像);下载 .msi 安装后如未被识别,请在设置页填写 Java 路径";
        }
        catch (Exception ex)
        {
            StatusText = "打开浏览器失败,请手动访问:" + url;
            Log($"[!] 打开浏览器失败:{ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private async Task LaunchAsync()
    {
        if (SelectedVersion is null)
            return;
        if (string.IsNullOrWhiteSpace(PlayerName))
        {
            StatusText = "请先填写玩家名";
            return;
        }

        var javaPath = JavaLocator.FindJava(_config.JavaDirectory);
        if (javaPath is null)
        {
            StatusText = "未找到 Java,请在设置页指定 Java 路径(自动下载运行时将在 M2 接入)";
            Log("[!] 未找到 Java:已检查 JAVA_HOME 与常见安装目录");
            return;
        }

        IsBusy = true;
        ProgressPercent = 0;
        StatusText = "正在准备游戏文件…";
        Log($"—— 启动 {SelectedVersion}(离线:{PlayerName},Java:{javaPath})——");

        // Progress<T> 在 UI 线程创建,回调自动调度回 UI 线程
        IProgress<LaunchProgress> uiProgress = new Progress<LaunchProgress>(p =>
        {
            if (p.TotalTasks > 0)
                ProgressPercent = Math.Max(ProgressPercent, p.Percent);
            if (!string.IsNullOrEmpty(p.CurrentFile))
                Log($"  下载 {p.CurrentFile}");
        });
        IProgress<string> uiLog = new Progress<string>(AppendLogCore);
        IProgress<object?> uiExit = new Progress<object?>(_ => OnGameExited());

        try
        {
            _gameProcess = await GetService().LaunchAsync(
                new LaunchRequest(
                    SelectedVersion, PlayerName.Trim(),
                    _config.GameDirectory, _config.Mirror,
                    _config.MaxMemoryMb, javaPath),
                uiProgress,
                line => uiLog.Report(line),
                () => uiExit.Report(null!));
            StatusText = "游戏已启动,日志见下方";
            ProgressPercent = 100;
            Log("[✓] 游戏进程已拉起");
        }
        catch (Exception ex)
        {
            StatusText = "启动失败:" + ex.Message;
            Log($"[!] 启动失败:{ex}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnGameExited()
    {
        StatusText = "游戏已退出";
        Log("[✓] 游戏进程已退出");
        _gameProcess = null;
    }

    private void Log(string line) => AppendLogCore(line);

    private void AppendLogCore(string line)
    {
        _logBuffer.AppendLine(line);
        if (_logBuffer.Length > 64_000)
        {
            var text = _logBuffer.ToString();
            var lines = text.Split('\n');
            var keep = lines[^MaxLogLines..];
            _logBuffer.Clear();
            foreach (var l in keep)
                _logBuffer.AppendLine(l.TrimEnd('\r'));
        }
        LogText = _logBuffer.ToString();
    }
}
