using System.IO;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using System.ComponentModel;
using CmlLib.Core.VersionMetadata;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Daybreak.Core.Game;
using Daybreak.Core.Mod;
using Daybreak.Core.Storage;
using Serilog;

namespace Daybreak.Launcher.ViewModels;

/// <summary>在线版本列表项,携带自己的安装进度(逐项进度条用)。</summary>
public partial class OnlineVersionItem : ObservableObject
{
    public required IVersionMetadata Metadata { get; init; }

    [ObservableProperty]
    private bool _isInstalling;

    /// <summary>0~100;0 表示总量未知(进度条走不确定模式)。</summary>
    [ObservableProperty]
    private int _installPercent;

    /// <summary>是否已完成且**校验通过**(与 IsInstalling 互斥,避免同时显示"已安装"和进度条)。</summary>
    [ObservableProperty]
    private bool _isInstalled;

    /// <summary>
    /// 「已安装」徽标的显示条件。
    ///
    /// 必须排除正在安装的情况:安装过程中 versions/&lt;名字&gt;/ 目录会很早被创建,
    /// 若仅按"目录存在"判断,列表会一边显示「已安装」一边还在下资源,
    /// 用户看到的就是"上面说装好了、下面还在跑"(实测踩过)。
    /// </summary>
    public bool ShowInstalledBadge => IsInstalled && !IsInstalling;

    partial void OnIsInstalledChanged(bool value) => OnPropertyChanged(nameof(ShowInstalledBadge));

    partial void OnIsInstallingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowInstalledBadge));
        OnPropertyChanged(nameof(ShowInstallButton));
        OnPropertyChanged(nameof(ButtonText));
    }

    /// <summary>「安装」按钮是否可用(已安装或正在安装时不可点)。</summary>
    public bool ShowInstallButton => !IsInstalled && !IsInstalling;

    /// <summary>按钮文案。安装中显示进度百分比,让用户知道没卡住。</summary>
    public string ButtonText => IsInstalling
        ? $"下载中 {InstallPercent}%"
        : IsInstalled ? "已安装" : "安装";

    partial void OnInstallPercentChanged(int value)
    {
        if (IsInstalling)
            OnPropertyChanged(nameof(ButtonText));
    }

    public string Name => Metadata.Name;
    public string TypeDisplay => Metadata.Type switch
    {
        "release" => "正式版",
        "snapshot" => "快照",
        "old_beta" => "Beta",
        "old_alpha" => "Alpha",
        var t => t ?? "未知",
    };
    public string TimeDisplay => Metadata.ReleaseTime.ToString("yyyy-MM-dd");
}

public partial class DownloadViewModel : ObservableObject
{
    private readonly LauncherConfig _config = LauncherConfig.Load();
    private LaunchService? _launchService;
    private LoaderService? _loaderService;
    private List<OnlineVersionItem> _allVersions = [];

    public ObservableCollection<OnlineVersionItem> Versions { get; } = [];
    public ObservableCollection<InstalledVersionInfo> InstalledVersions { get; } = [];

    /// <summary>false = 只显示正式版。</summary>
    [ObservableProperty]
    private bool _showAll;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "点击刷新获取版本列表";

    /// <summary>底部进度条 0~100;0 = 不确定。</summary>
    [ObservableProperty]
    private int _progressPercent;

    [ObservableProperty]
    private string? _activeDownloadName;

    [ObservableProperty]
    private string? _currentFile;

    [ObservableProperty]
    private string _downloadedBytesDisplay = "";

    /// <summary>当前下载速度(MB/s,保留一位小数)。</summary>
    [ObservableProperty]
    private string _speedDisplay = "";

    private string _statusSuffix = "";
    private long _lastReportedBytes = -1;
    private DateTime _lastSpeedSampleTime = DateTime.MinValue;

    // ---- 进度节流 ----
    // CmlLib 一次安装会回报上万次(文件事件 + 字节事件)。若每次都经 Progress<T> Post 到 UI 线程,
    // 切到别的页面时队列会积压;回到下载页时表现为"进度突然跳到 100%/直接显示完成",
    // 看起来就像下载被中断了(实测就是这个现象)。
    // 改为:后台记下最新值,由定时器每 200ms 取一次快照刷新 UI。
    private readonly object _progressGate = new();
    private LaunchProgress? _pendingProgress;
    private bool _pendingIsByteBased;
    private System.Windows.Threading.DispatcherTimer? _progressTimer;
    private DateTime _installStartTime = DateTime.MinValue;

    /// <summary>是否有安装在后台进行(切到别的页面后据此提示用户)。</summary>
    [ObservableProperty]
    private bool _hasBackgroundInstall;

    /// <summary>安装开始时间(用于估算已用时)。</summary>
    [ObservableProperty]
    private string _installElapsedDisplay = "";

    /// <summary>正在安装的版本名(切页面后仍显示)。</summary>
    [ObservableProperty]
    private string? _backgroundInstallName;

    // ===== 加载器安装(M3) =====

    public ObservableCollection<string> LoaderModes { get; } = ["原版", "Fabric", "Forge", "NeoForge"];

    public ObservableCollection<string> GameVersions { get; } = [];

    public ObservableCollection<LoaderVersionInfo> LoaderVersions { get; } = [];

    [ObservableProperty]
    private int _selectedLoaderIndex;

    [ObservableProperty]
    private string? _selectedGameVersion;

    [ObservableProperty]
    private LoaderVersionInfo? _selectedLoaderVersion;

    [ObservableProperty]
    private bool _versionIsolation;

    public bool IsLoaderMode => SelectedLoaderIndex > 0;

    public bool ShowLoaderVersionCombo => SelectedLoaderIndex == 1 || SelectedLoaderIndex == 3;

    public DownloadViewModel()
    {
        _versionIsolation = _config.VersionIsolation;
        _ = RefreshAsync();
    }

    partial void OnShowAllChanged(bool value) => ApplyFilter();
    partial void OnIsBusyChanged(bool value)
    {
        InstallCommand.NotifyCanExecuteChanged();
        InstallLoaderCommand.NotifyCanExecuteChanged();
    }
    partial void OnVersionIsolationChanged(bool value)
    {
        _config.VersionIsolation = value;
        _config.Save();
    }

    partial void OnSelectedLoaderIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsLoaderMode));
        OnPropertyChanged(nameof(ShowLoaderVersionCombo));
        SelectedLoaderVersion = null;
        LoaderVersions.Clear();
        if (IsLoaderMode)
        {
            FillGameVersions();
            _ = LoadLoaderVersionsAsync();
        }
    }

    partial void OnSelectedGameVersionChanged(string? value)
    {
        if (SelectedLoaderIndex == 2 || SelectedLoaderIndex == 3)
        {
            SelectedLoaderVersion = null;
            LoaderVersions.Clear();
            _ = LoadLoaderVersionsAsync();
        }
    }

    private void FillGameVersions()
    {
        var current = SelectedGameVersion;
        GameVersions.Clear();
        foreach (var name in _allVersions.Where(v => v.Metadata.Type == "release").Select(v => v.Name))
            GameVersions.Add(name);
        SelectedGameVersion = GameVersions.Contains(current) ? current : GameVersions.FirstOrDefault();
    }

    private LaunchService GetService() =>
        _launchService ??= new LaunchService(_config.GameDirectory, _config.Mirror, _config.VersionIsolation);

    private LoaderService GetLoaderService() =>
        _loaderService ??= new LoaderService(_config.GameDirectory, _config.Mirror);

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        StatusText = "正在获取版本清单…";
        try
        {
            var metadataList = await GetService().GetOnlineVersionsAsync();

            // 用"真正装完"而不是"目录存在"来判断已安装:
            // 安装中途 CmlLib 会先写 json,只按目录判断会把没装完的版本标成「已安装」
            var service = GetService();
            _allVersions = metadataList
                .Select(m => new OnlineVersionItem
                {
                    Metadata = m,
                    IsInstalled = service.IsVersionComplete(m.Name),
                })
                .ToList();

            ApplyFilter();
            RefreshInstalledList();
            if (IsLoaderMode)
                FillGameVersions();
            _statusSuffix = $"({_config.Mirror switch { "bmclapi" => "BMCLAPI 镜像", _ => "官方源" }})";
            StatusText = $"共 {_allVersions.Count} 个版本{_statusSuffix}";
            Log.Information("版本清单获取成功:{Count} 个版本", _allVersions.Count);
        }
        catch (Exception ex)
        {
            StatusText = "获取版本清单失败:" + ex.Message;
            Log.Error(ex, "版本清单获取失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyFilter()
    {
        Versions.Clear();
        foreach (var item in _allVersions.Where(v => ShowAll || v.Metadata.Type == "release"))
            Versions.Add(item);
    }

    private void RefreshInstalledList()
    {
        try
        {
            var infos = GetService().GetInstalledVersionInfos();
            InstalledVersions.Clear();
            foreach (var info in infos)
                InstalledVersions.Add(info);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "已安装版本列表读取失败");
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F2} GB",
        >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
        _ => $"{bytes / 1_024.0:F0} KB",
    };

    /// <summary>处理进度更新并计算下载速度。</summary>
    private void HandleProgress(LaunchProgress p, bool isByteBased)
    {
        if (p.TotalTasks > 0)
        {
            // 取最大值防止进度回退(CmlLib 的阶段切换会让 Percent 短暂变小)
            var percent = Math.Max(ProgressPercent, p.Percent);

            // 正在安装的那一项也要跟着走,否则切页面回来该行的进度条是空的
            if (ActiveDownloadName is not null)
            {
                var active = Versions.FirstOrDefault(v =>
                    string.Equals(v.Name, ActiveDownloadName, StringComparison.OrdinalIgnoreCase));
                if (active is not null)
                    active.InstallPercent = percent;
            }

            ProgressPercent = percent;
        }

        CurrentFile = p.CurrentFile;

        if (isByteBased && p.TotalBytes > 0)
        {
            DownloadedBytesDisplay =
                $"{FormatBytes(p.ProgressedBytes)} / {FormatBytes(p.TotalBytes)}";

            // 计算速度:每 1 秒采样一次
            var now = DateTime.UtcNow;
            if (_lastReportedBytes >= 0 && _lastSpeedSampleTime != DateTime.MinValue)
            {
                var elapsed = (now - _lastSpeedSampleTime).TotalSeconds;
                if (elapsed >= 1.0)
                {
                    var deltaBytes = p.ProgressedBytes - _lastReportedBytes;
                    var speedMbps = (deltaBytes / elapsed) / (1024.0 * 1024.0);
                    SpeedDisplay = $"↑{speedMbps:F1} MB/s";
                }
            }
            _lastReportedBytes = p.ProgressedBytes;
            _lastSpeedSampleTime = now;
        }
        else if (!isByteBased)
        {
            DownloadedBytesDisplay = "";
            SpeedDisplay = "";
            _lastReportedBytes = -1;
            _lastSpeedSampleTime = DateTime.MinValue;
        }
    }

    private bool CanInstall() => !IsBusy;

    /// <summary>
    /// 后台线程写入最新进度(不碰 UI 属性)。
    /// 由 <see cref="EnsureProgressTimer"/> 每 200ms 取一次快照刷新界面,
    /// 这样无论用户切到哪个页面,进度都不会丢、也不会把 UI 线程压垮。
    /// </summary>
    private void QueueProgress(LaunchProgress progress, bool isByteBased)
    {
        lock (_progressGate)
        {
            _pendingProgress = progress;
            _pendingIsByteBased = isByteBased;
        }
    }

    private void EnsureProgressTimer()
    {
        if (_progressTimer is not null)
            return;

        _progressTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _progressTimer.Tick += (_, _) => DrainProgress();
        _progressTimer.Start();
    }

    /// <summary>把后台积压的进度快照刷到 UI(每 200ms 一次)。</summary>
    private void DrainProgress()
    {
        LaunchProgress? snapshot;
        bool isByteBased;

        lock (_progressGate)
        {
            snapshot = _pendingProgress;
            isByteBased = _pendingIsByteBased;
            _pendingProgress = null;
        }

        if (snapshot is not null)
            HandleProgress(snapshot, isByteBased);

        // 已用时:安装进行中时每秒刷新
        if (_installStartTime != DateTime.MinValue)
        {
            var elapsed = DateTime.Now - _installStartTime;
            InstallElapsedDisplay = elapsed.TotalSeconds < 60
                ? $"已用 {elapsed.TotalSeconds:F0} 秒"
                : $"已用 {elapsed.TotalMinutes:F1} 分钟";
        }
    }

    private void StopProgressTimer()
    {
        _progressTimer?.Stop();
        _progressTimer = null;
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync(OnlineVersionItem? item)
    {
        if (item is null || item.IsInstalled || item.IsInstalling)
            return;

        IsBusy = true;
        item.IsInstalling = true;
        item.InstallPercent = 0;
        ProgressPercent = 0;
        ActiveDownloadName = item.Name;
        BackgroundInstallName = item.Name;
        HasBackgroundInstall = true;
        CurrentFile = null;
        DownloadedBytesDisplay = "";
        SpeedDisplay = "";
        StatusText = $"正在安装 {item.Name}…";
        _installStartTime = DateTime.Now;
        InstallElapsedDisplay = "已用 0 秒";
        EnsureProgressTimer();
        Log.Information("开始安装 {Version}", item.Name);

        // 进度回调在后台线程触发,这里只记录最新值,由 200ms 定时器刷新 UI。
        // 不直接用 Progress<T>:它会把每次回报 Post 到 UI 线程,上万次回报在切页面时
        // 会积压成"进度突然跳满",看起来像下载被打断。
        var progress = new Progress<LaunchProgress>(p => QueueProgress(p, isByteBased: true));

        try
        {
            await Task.Run(() => GetService().InstallAsync(item.Name, _config.VersionIsolation, progress));

            // 安装完成后必须确认核心文件真的到位,再标记"已安装"。
            // 之前无条件标记,一旦中途静默失败就会显示"安装完成"但实际不可用(假完成)。
            var verification = VerifyInstalled(item.Name);
            if (!verification.Ok)
            {
                item.IsInstalled = false;
                item.InstallPercent = 0;
                ProgressPercent = 0;
                StatusText = $"{item.Name} 安装不完整:{verification.Reason}。请点「安装」重试。";
                Log.Warning("{Version} 安装后校验未通过:{Reason}", item.Name, verification.Reason);
                return;
            }

            item.IsInstalled = true;
            item.InstallPercent = 100;
            ProgressPercent = 100;
            StatusText = $"{item.Name} 安装完成{_statusSuffix}";
            Log.Information("{Version} 安装完成", item.Name);
            RefreshInstalledList();
        }
        catch (Exception ex)
        {
            item.IsInstalled = false;
            item.InstallPercent = 0;
            ProgressPercent = 0;
            StatusText = $"安装 {item.Name} 失败:" + ex.Message;
            Log.Error(ex, "{Version} 安装失败", item.Name);
        }
        finally
        {
            item.IsInstalling = false;
            IsBusy = false;
            CurrentFile = null;
            ActiveDownloadName = null;
            BackgroundInstallName = null;
            HasBackgroundInstall = false;
            DownloadedBytesDisplay = "";
            SpeedDisplay = "";
            InstallElapsedDisplay = "";
            _installStartTime = DateTime.MinValue;
            DrainProgress();
            StopProgressTimer();

            // 清理下载中断留下的 .part 残留(实测会在 libraries 下堆积)
            CleanupPartFiles();
        }
    }

    /// <summary>
    /// 校验某版本是否真的装好。
    /// 直接复用 Core 的 IsVersionComplete(版本 JSON + 客户端 jar + 无 .part 残留),
    /// 避免"看着装完了其实缺文件"。
    /// </summary>
    private (bool Ok, string Reason) VerifyInstalled(string versionName)
    {
        try
        {
            if (GetService().IsVersionComplete(versionName))
                return (true, "");

            var dir = Path.Combine(GetService().CreateGamePath().Versions, versionName);
            if (!File.Exists(Path.Combine(dir, versionName + ".json")))
                return (false, "版本 JSON 缺失");

            if (Directory.Exists(dir) &&
                Directory.EnumerateFiles(dir, "*.part", SearchOption.TopDirectoryOnly).Any())
                return (false, "仍有未完成的下载文件(.part)");

            return (false, "客户端 jar 缺失或未下载完整");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>清理游戏目录下的 .part 残留(多加一层保护,失败不影响主流程)。</summary>
    private void CleanupPartFiles()
    {
        try
        {
            var path = GetService().CreateGamePath();
            var removed = 0;

            // MinecraftPath 只暴露 BasePath/Versions,libraries 需要自己拼
            foreach (var root in new[]
                     {
                         Path.Combine(path.BasePath, "libraries"),
                         path.Versions,
                         path.BasePath,
                     })
            {
                if (!Directory.Exists(root))
                    continue;

                foreach (var file in Directory.EnumerateFiles(root, "*.part", SearchOption.AllDirectories))
                {
                    try
                    {
                        File.Delete(file);
                        removed++;
                    }
                    catch (IOException)
                    {
                        // 仍被占用就跳过,下次再清
                    }
                }
            }

            if (removed > 0)
                Log.Information("已清理 {Count} 个下载残留(.part)", removed);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "清理 .part 残留失败");
        }
    }

    // ===== 加载器安装 =====

    private async Task LoadLoaderVersionsAsync()
    {
        if (SelectedLoaderIndex == 0 || string.IsNullOrEmpty(SelectedGameVersion) || IsBusy)
            return;

        IsBusy = true;
        StatusText = $"正在获取 {(LoaderModes[SelectedLoaderIndex])} 版本列表…";
        try
        {
            var versions = SelectedLoaderIndex switch
            {
                1 => (await GetLoaderService().GetFabricLoaderVersionsAsync())
                    .Select(n => new LoaderVersionInfo(n, false, false)),
                2 => await GetLoaderService().GetForgeVersionsAsync(SelectedGameVersion!),
                3 => await GetLoaderService().GetNeoForgeVersionsAsync(SelectedGameVersion!),
                _ => throw new InvalidOperationException(),
            };

            LoaderVersions.Clear();
            foreach (var version in versions)
                LoaderVersions.Add(version);

            SelectedLoaderVersion = versions.FirstOrDefault();
            StatusText = $"{LoaderModes[SelectedLoaderIndex]} 共 {LoaderVersions.Count} 个可用版本{_statusSuffix}";
        }
        catch (Exception ex)
        {
            StatusText = $"获取 {LoaderModes[SelectedLoaderIndex]} 版本列表失败:" + ex.Message;
            Log.Warning(ex, "加载器版本列表获取失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanInstallLoader() =>
        !IsBusy && IsLoaderMode &&
        !string.IsNullOrEmpty(SelectedGameVersion) && SelectedLoaderVersion is not null;

    [RelayCommand(CanExecute = nameof(CanInstallLoader))]
    private async Task InstallLoaderAsync()
    {
        if (SelectedGameVersion is null || SelectedLoaderVersion is null)
            return;

        IsBusy = true;
        ProgressPercent = 0;
        CurrentFile = null;
        DownloadedBytesDisplay = "";
        var loaderName = LoaderModes[SelectedLoaderIndex];
        ActiveDownloadName = $"{loaderName} {SelectedGameVersion}";
        StatusText = $"正在安装 {ActiveDownloadName}…";
        Log.Information("开始安装加载器 {Loader} {Game} {LoaderVersion}",
            loaderName, SelectedGameVersion, SelectedLoaderVersion.Name);

        IProgress<LaunchProgress> uiProgress = new Progress<LaunchProgress>(p =>
        {
            if (p.TotalTasks > 0)
            {
                var percent = Math.Max(ProgressPercent, p.Percent);
                ProgressPercent = percent;
            }
            HandleProgress(p, isByteBased: true);
        });

        try
        {
            var javaPath = await GetService().EnsureJavaAsync(
                SelectedGameVersion, _config.JavaDirectory, uiProgress);

            var newVersionName = SelectedLoaderIndex switch
            {
                1 => await GetLoaderService().InstallFabricAsync(
                    SelectedGameVersion, SelectedLoaderVersion.Name, uiProgress),
                2 => await GetLoaderService().InstallForgeAsync(
                    SelectedGameVersion, SelectedLoaderVersion.Name, javaPath, uiProgress),
                3 => await GetLoaderService().InstallNeoForgeAsync(
                    SelectedGameVersion, SelectedLoaderVersion.Name, javaPath, uiProgress),
                _ => throw new InvalidOperationException(),
            };

            StatusText = $"{loaderName} 安装完成,新版本:{newVersionName}";
            Log.Information("加载器安装完成:{Version}", newVersionName);
            RefreshInstalledList();
        }
        catch (Exception ex)
        {
            StatusText = $"{loaderName} 安装失败:" + ex.Message;
            Log.Error(ex, "{Loader} 安装失败", loaderName);
        }
        finally
        {
            IsBusy = false;
            ActiveDownloadName = null;
            CurrentFile = null;
            DownloadedBytesDisplay = "";
        }
    }
}
