using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Daybreak.Core.Game;
using Daybreak.Core.Storage;
using Daybreak.Launcher.ViewModels;
using Serilog;

namespace Daybreak.Launcher.ViewModels;

public partial class HomeViewModel : ObservableObject
{
    private const int MaxLogLines = 400;

    private readonly LauncherConfig _config = LauncherConfig.Load();
    private readonly StringBuilder _logBuffer = new();
    private readonly object _logLock = new();
    private bool _logDirty;
    private readonly System.Windows.Threading.DispatcherTimer _logFlushTimer;
    private LaunchService? _launchService;
    private Process? _gameProcess;

    public ObservableCollection<string> InstalledVersions { get; } = [];

    public AuthViewModel Auth { get; } = App.GetService<AuthViewModel>();

    [ObservableProperty]
    private string? _selectedVersion;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "就绪";

    /// <summary>0~100;0 表示无法确定总量(显示不确定进度)。</summary>
    [ObservableProperty]
    private int _progressPercent;

    [ObservableProperty]
    private string _logText = "";

    private string? _lastProgressFile;

    public HomeViewModel()
    {
        // 日志节流:下载期间每秒可产生数百条文件事件,若每条都重建 LogText
        // 并触发 TextBox 全量重排 + ScrollToEnd,UI 线程会被文本格式化淹死
        _logFlushTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300),
        };
        _logFlushTimer.Tick += (_, _) =>
        {
            if (!_logDirty)
                return;
            _logDirty = false;
            TrimLogBuffer();
            LogText = _logBuffer.ToString();
        };
        _logFlushTimer.Start();

        _ = LoadVersionsAsync();
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

    /// <summary>启动前确保有版本兼容的 Java(探测/校验/自动下载都在 Core 完成)。</summary>
    private async Task<string?> EnsureJavaAsync(string versionId)
    {
        StatusText = "正在准备 Java 运行时…";
        IProgress<LaunchProgress> javaProgress = new Progress<LaunchProgress>(p =>
        {
            if (p.TotalTasks > 0)
                ProgressPercent = Math.Max(ProgressPercent, p.Percent);
            if (!string.IsNullOrEmpty(p.CurrentFile))
                Log($"  下载 {p.CurrentFile}");
        });

        var runtimeDir = await GetService().EnsureJavaAsync(versionId, _config.JavaDirectory, javaProgress);
        if (runtimeDir is not null)
            Log($"[✓] Java 就绪:{runtimeDir}");
        return runtimeDir;
    }

    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private async Task LaunchAsync()
    {
        if (SelectedVersion is null)
            return;

        IsBusy = true;
        ProgressPercent = 0;
        Log($"—— 启动 {SelectedVersion} ——");

        try
        {
            var javaPath = await EnsureJavaAsync(SelectedVersion);
            if (javaPath is null)
            {
                StatusText = "Java 自动下载失败,请到「下载 Java」手动获取";
                return;
            }

            var auth = await Auth.GetLaunchAuthAsync(text =>
            {
                StatusText = text;
                Log(text);
            });
            Log($"[✓] 认证就绪:{Auth.ActiveAccountDisplay}");

            StatusText = "正在准备游戏文件(首次运行需下载资源,可能较久)…";
            long lastLoggedBytes = 0;
            var fileEventCount = 0;
            IProgress<LaunchProgress> uiProgress = new Progress<LaunchProgress>(p =>
            {
                if (p.TotalTasks > 0)
                    ProgressPercent = Math.Max(ProgressPercent, p.Percent);

                // 字节进度节流上状态栏(每 8MB 刷一次,避免刷屏)
                if (p.TotalBytes > 0 && p.ProgressedBytes - lastLoggedBytes > 8 * 1024 * 1024)
                {
                    lastLoggedBytes = p.ProgressedBytes;
                    StatusText = $"正在下载游戏文件 {p.ProgressedBytes / 1_048_576} MB / {p.TotalBytes / 1_048_576} MB…";
                }

                // 文件事件只在日志里留抽样(大版本有数千文件,全记会淹掉日志)
                if (!string.IsNullOrEmpty(p.CurrentFile) && p.CurrentFile != _lastProgressFile)
                {
                    _lastProgressFile = p.CurrentFile;
                    fileEventCount++;
                    if (fileEventCount % 25 == 1)
                        Log($"  文件进度 {fileEventCount}: {p.CurrentFile}");
                }
            });
            IProgress<string> uiLog = new Progress<string>(AppendLogCore);
            IProgress<int> uiExit = new Progress<int>(OnGameExited);

            // Task.Run 脱离 UI 线程同步上下文:CmlLib 内部存在同步等待异步的代码,
            // 在 UI 线程上下文中调用会死锁(表现为"正在准备游戏文件…"卡住)
            _gameProcess = await Task.Run(async () => await GetService().LaunchAsync(
                new LaunchRequest(
                    SelectedVersion, Auth.PlayerName.Trim(),
                    _config.GameDirectory, _config.Mirror,
                    _config.MaxMemoryMb, javaPath)
                {
                    Session = auth.Session,
                    JavaAgentJarPath = auth.JavaAgentJar,
                    AuthServerUrl = auth.AuthServerUrl,
                },
                uiProgress,
                line => uiLog.Report(line),
                () => uiExit.Report(_gameProcess?.ExitCode ?? -1)));
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

    private void OnGameExited(int exitCode)
    {
        _gameProcess = null;

        if (exitCode == 0)
        {
            StatusText = "游戏正常退出";
            Log("[✓] 游戏正常退出");
            return;
        }

        StatusText = $"游戏异常退出(退出码 {exitCode}),正在诊断…";
        Log($"[!] 游戏异常退出,退出码 {exitCode},开始崩溃诊断");

        var version = SelectedVersion;
        var gameRoot = GetService().ResolveGameRootForVersion(version!);
        IProgress<Core.Diagnostics.CrashReport> uiReport =
            new Progress<Core.Diagnostics.CrashReport>(report =>
            {
                if (report.CrashReportPath is not null)
                    Log($"崩溃报告:{report.CrashReportPath}");

                foreach (var finding in report.Findings)
                    Log($"【诊断】{finding.Title} —— {finding.Advice}");

                StatusText = report.Findings.Count > 0
                    ? $"游戏崩溃:{report.Findings[0].Title}(详见日志与下方诊断)"
                    : $"游戏异常退出(退出码 {exitCode})";
            });

        _ = Task.Run(() => uiReport.Report(Core.Diagnostics.CrashAnalyzer.Analyze(gameRoot, exitCode)));
    }

    private void Log(string line)
    {
        lock (_logLock)
        {
            _logBuffer.AppendLine(line);
            if (_logBuffer.Length > 128_000)
            {
                var text = _logBuffer.ToString();
                var lines = text.Split('\n');
                var keep = lines[^MaxLogLines..];
                _logBuffer.Clear();
                foreach (var l in keep)
                    _logBuffer.AppendLine(l.TrimEnd('\r'));
            }
            _logDirty = true;
        }
    }

    private void TrimLogBuffer()
    {
        lock (_logLock)
        {
            if (_logBuffer.Length <= 128_000)
                return;
            var text = _logBuffer.ToString();
            var lines = text.Split('\n');
            var keep = lines[^MaxLogLines..];
            _logBuffer.Clear();
            foreach (var l in keep)
                _logBuffer.AppendLine(l.TrimEnd('\r'));
        }
    }

    private void AppendLogCore(string line) => Log(line);
}
