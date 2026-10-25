using System.Diagnostics;
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

    /// <summary>主题模式:0=深色,1=浅色,2=跟随系统。</summary>
    [ObservableProperty]
    private int _selectedThemeIndex;

    /// <summary>背景效果:0=Acrylic,1=Mica,2=None。</summary>
    [ObservableProperty]
    private int _selectedBackdropIndex;

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
    private string _microsoftClientId = "";

    [ObservableProperty]
    private string _jvmArguments = "";

    [ObservableProperty]
    private string _proxyHost = "";

    [ObservableProperty]
    private int _proxyPort;

    [ObservableProperty]
    private int _selectedProxyTypeIndex; // 0=HTTP,1=SOCKS5

    [ObservableProperty]
    private bool _proxyEnabled;

    [ObservableProperty]
    private string _settingsStatusText = "";

    public LauncherConfig Config => _config;

    public SettingsViewModel()
    {
        // 主题: Dark=0, Light=1, System=2
        _selectedThemeIndex = _config.ThemeMode switch
        {
            "Light" => 1,
            "System" => 2,
            _ => 0, // Dark
        };
        // 背景效果: Acrylic=0, Mica=1, None=2
        _selectedBackdropIndex = _config.Backdrop switch
        {
            "Mica" => 1,
            "Tabbed" => 2, // Treat Tabbed as None for simplicity
            "None" => 2,
            _ => 0, // Acrylic
        };
        _gameDirectory = _config.GameDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
        _javaDirectory = _config.JavaDirectory ?? "(自动探测)";
        _maxMemoryMb = _config.MaxMemoryMb;
        _selectedMirrorIndex = _config.Mirror == "bmclapi" ? 1 : 0;
        _versionIsolation = _config.VersionIsolation;
        _microsoftClientId = _config.MicrosoftClientId ?? "";
        _jvmArguments = _config.JvmArguments ?? "";
        _proxyHost = _config.ProxyHost ?? "";
        _proxyPort = _config.ProxyPort;
        _selectedProxyTypeIndex = _config.ProxyType == "SOCKS5" ? 1 : 0;
        _proxyEnabled = !string.IsNullOrWhiteSpace(_config.ProxyHost) && _config.ProxyPort > 0;
    }

    partial void OnSelectedThemeIndexChanged(int value)
    {
        var theme = value switch
        {
            1 => "Light",
            2 => "System",
            _ => "Dark", // 0
        };
        var backdrop = _selectedBackdropIndex switch
        {
            1 => "Mica",
            2 => "None",
            _ => "Acrylic",
        };
        Services.ThemeService.ApplyThemeMode(theme, backdrop);
        _config.ThemeMode = theme;
        _config.Backdrop = backdrop;
        _config.Save();
        Log.Information("主题切换为 {Theme},背景 {Backdrop}", theme, backdrop);
    }

    partial void OnSelectedBackdropIndexChanged(int value)
    {
        var backdrop = value switch
        {
            1 => "Mica",
            2 => "None",
            _ => "Acrylic",
        };
        var theme = _selectedThemeIndex switch
        {
            1 => "Light",
            2 => "System",
            _ => "Dark",
        };
        Services.ThemeService.ApplyThemeMode(theme, backdrop);
        _config.Backdrop = backdrop;
        _config.Save();
        Log.Information("背景效果切换为 {Backdrop}", backdrop);
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

    [RelayCommand]
    private void SaveMicrosoftClientId()
    {
        var id = MicrosoftClientId.Trim();
        if (id.Length == 0)
        {
            _config.MicrosoftClientId = null;
            _config.Save();
            SettingsStatusText = "已清除 ClientId,微软登录将使用开发期备用 ID(发布前请替换)";
            Log.Information("微软 ClientId 已清除");
        }
        else
        {
            // 合法 GUID 格式:8-4-4-4-12 十六进制
            if (!System.Text.RegularExpressions.Regex.IsMatch(id,
                    @"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$"))
            {
                SettingsStatusText = "ClientId 格式不正确(应为 GUID,如 xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx)";
                return;
            }

            _config.MicrosoftClientId = id;
            _config.Save();
            SettingsStatusText = "微软 ClientId 已保存(重启启动器后生效)";
            Log.Information("微软 ClientId 已更新");
        }

        // 同步更新 AuthViewModel
        App.GetService<AuthViewModel>().LoadFromConfig();
    }

    [RelayCommand]
    private void SaveJvmArguments()
    {
        var args = JvmArguments.Trim();
        if (args.Length == 0)
        {
            _config.JvmArguments = null;
            _config.Save();
            SettingsStatusText = "已清除自定义 JVM 参数";
            Log.Information("JVM 参数已清除");
            return;
        }

        // 检查是否包含 -Xmx(与内存设置冲突)
        if (args.Contains("-Xmx", StringComparison.OrdinalIgnoreCase))
        {
            SettingsStatusText = "警告:检测到 -Xmx 参数,这与内存设置冲突。建议使用\"游戏内存(MB)\"字段来设置。";
            // 仍然保存,但给提示
        }

        _config.JvmArguments = args;
        _config.Save();
        SettingsStatusText = "JVM 参数已保存(下次启动生效)";
        Log.Information("JVM 参数已更新: {Args}", args);
    }

    [RelayCommand]
    private void SaveProxy()
    {
        var host = ProxyHost.Trim();
        if (string.IsNullOrEmpty(host) || ProxyPort <= 0)
        {
            _config.ProxyHost = null;
            _config.ProxyPort = 0;
            _config.Save();
            SettingsStatusText = "已清除代理设置";
            Log.Information("代理已清除");
            return;
        }

        // 端口范围校验
        if (ProxyPort is < 1 or > 65535)
        {
            SettingsStatusText = "代理端口需在 1 ~ 65535 之间";
            return;
        }

        _config.ProxyHost = host;
        _config.ProxyPort = ProxyPort;
        _config.ProxyType = _selectedProxyTypeIndex == 1 ? "SOCKS5" : "HTTP";
        _config.Save();
        SettingsStatusText = $"代理已保存({_config.ProxyType}://{host}:{ProxyPort},下次下载生效)";
        Log.Information("代理已更新: {Type}://{Host}:{Port}", _config.ProxyType, host, ProxyPort);
    }

    partial void OnProxyEnabledChanged(bool value)
    {
        // 复选框切换时自动启用/禁用输入框(通过 UI 的 IsEnabled 绑定处理,这里只同步配置)
        if (!value)
        {
            _config.ProxyHost = null;
            _config.ProxyPort = 0;
            _config.Save();
        }
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

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        SettingsStatusText = "正在检查更新…";
        try
        {
            var updaterExe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Daybreak.Updater.exe");
            if (!File.Exists(updaterExe))
            {
                SettingsStatusText = "未找到 Daybreak.Updater.exe(更新器需与主程序同目录,内测包已含)";
                return;
            }

            var (exitCode, stdout, stderr) = await RunUpdaterCheckAsync(updaterExe);

            // 0 = 已是最新;2 = 有新版本;其它 = 失败
            if (exitCode != 2)
            {
                if (exitCode == 0)
                {
                    SettingsStatusText = $"已是最新版本(当前 {CurrentAppVersion()})";
                }
                else
                {
                    var detail = FirstNonEmptyLine(stderr) ?? FirstNonEmptyLine(stdout) ?? $"退出码 {exitCode}";
                    SettingsStatusText = "检查更新失败:" + detail;
                    Log.Warning("检查更新失败:退出码 {Code},{Detail}", exitCode, detail);
                }
                return;
            }

            var (newVersion, downloadUrl) = ParseNewVersionLine(stdout);
            if (string.IsNullOrEmpty(newVersion) || string.IsNullOrEmpty(downloadUrl))
            {
                SettingsStatusText = "检测到新版本,但无法解析下载地址。请到 Releases 页面手动下载。";
                Log.Warning("更新输出解析失败:{Out}", stdout);
                return;
            }

            Log.Information("发现新版本 {Version}:{Url}", newVersion, downloadUrl);
            SettingsStatusText = $"发现新版本 {newVersion}";

            var confirm = System.Windows.MessageBox.Show(
                $"发现新版本 {newVersion}\n" +
                $"当前版本:{CurrentAppVersion()}\n\n" +
                "更新将下载新版本压缩包(约 77 MB)并自动重启启动器。\n" +
                "下载期间会弹出命令行窗口显示进度,请勿手动关闭。\n\n" +
                "是否立即更新?",
                "发现新版本",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);

            if (confirm != System.Windows.MessageBoxResult.Yes)
            {
                SettingsStatusText = $"已跳过更新(新版本 {newVersion} 可随时重新检查)";
                return;
            }

            StartUpdateAndShutdown(updaterExe, downloadUrl, newVersion);
        }
        catch (Exception ex)
        {
            SettingsStatusText = "检查更新失败:" + ex.Message;
            Log.Error(ex, "检查更新异常");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 运行更新器的 --check。
    /// 必须先并发读取 stdout/stderr 再等待退出:只等待会让子进程在管道缓冲区写满时阻塞
    /// (典型死锁),之后再 ReadToEnd 会永远等不到。
    /// </summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunUpdaterCheckAsync(string updaterExe)
    {
        var psi = new ProcessStartInfo
        {
            FileName = updaterExe,
            Arguments = "--check",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(updaterExe)!,
        };

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动更新器");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        // 更新器内部网络超时为 30s,这里给足余量避免误判挂死
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
            throw new TimeoutException("更新器检查超时(90 秒),请检查网络或代理设置");
        }

        return (proc.ExitCode, (await stdoutTask).Trim(), (await stderrTask).Trim());
    }

    /// <summary>从更新器输出中提取 "NEW:&lt;version&gt;:&lt;url&gt;" 行(URL 本身含冒号,需整段保留)。</summary>
    private static (string? Version, string? Url) ParseNewVersionLine(string stdout)
    {
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("NEW:", StringComparison.Ordinal))
                continue;

            var rest = line["NEW:".Length..];
            var sep = rest.IndexOf(':');
            if (sep <= 0)
                continue;

            var version = rest[..sep].Trim();
            var url = rest[(sep + 1)..].Trim();
            if (version.Length > 0 && url.Length > 0)
                return (version, url);
        }
        return (null, null);
    }

    private static string? FirstNonEmptyLine(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0)
                return line;
        }
        return null;
    }

    /// <summary>当前程序版本(单文件发布时 Environment.ProcessPath 即主程序 exe)。</summary>
    private static string CurrentAppVersion()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                var fileVersion = FileVersionInfo.GetVersionInfo(path).FileVersion;
                if (!string.IsNullOrWhiteSpace(fileVersion))
                    return fileVersion;
            }
        }
        catch
        {
            // 读不到版本不影响主流程
        }
        return "未知";
    }

    /// <summary>
    /// 启动更新器执行 --update,随后退出主程序。
    /// 顺序很关键:更新器会先下载,再等待主程序进程消失后才覆盖文件,
    /// 所以这里必须真的退出,否则更新器会一直等。
    /// </summary>
    private void StartUpdateAndShutdown(string updaterExe, string downloadUrl, string newVersion)
    {
        Log.Information("开始一键更新到 {Version}", newVersion);

        // UseShellExecute=true:让更新器在独立控制台窗口里显示下载进度
        var psi = new ProcessStartInfo
        {
            FileName = updaterExe,
            Arguments = $"--update \"{downloadUrl}\"",
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(updaterExe)!,
        };
        Process.Start(psi);

        SettingsStatusText = $"正在更新到 {newVersion},启动器即将关闭…";

        // 留一小段时间让状态文本渲染,再退出让更新器接管文件替换
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1200),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            System.Windows.Application.Current?.Shutdown();
        };
        timer.Start();
    }

    /// <summary>是否正在执行操作(用于按钮禁用)。</summary>
    [ObservableProperty]
    private bool _isBusy;
}
