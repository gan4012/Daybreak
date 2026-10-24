using System.Collections.ObjectModel;
using System.ComponentModel;
using CmlLib.Core.VersionMetadata;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Daybreak.Core.Game;
using Daybreak.Core.Storage;
using Serilog;

namespace Daybreak.Launcher.ViewModels;

/// <summary>在线版本列表项,携带自己的安装进度(逐项进度条用)。</summary>
public partial class OnlineVersionItem : ObservableObject
{
    public required IVersionMetadata Metadata { get; init; }

    [ObservableProperty]
    private bool _isInstalled;

    [ObservableProperty]
    private bool _isInstalling;

    /// <summary>0~100;0 表示总量未知(进度条走不确定模式)。</summary>
    [ObservableProperty]
    private int _installPercent;

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

    private string _statusSuffix = "";

    public DownloadViewModel() => _ = RefreshAsync();

    partial void OnShowAllChanged(bool value) => ApplyFilter();
    partial void OnIsBusyChanged(bool value) => InstallCommand.NotifyCanExecuteChanged();

    private LaunchService GetService() =>
        _launchService ??= new LaunchService(_config.GameDirectory, _config.Mirror);

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
            var installed = GetService().GetInstalledVersions().ToHashSet(StringComparer.OrdinalIgnoreCase);

            _allVersions = metadataList
                .Select(m => new OnlineVersionItem
                {
                    Metadata = m,
                    IsInstalled = installed.Contains(m.Name),
                })
                .ToList();

            ApplyFilter();
            RefreshInstalledList();
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

    private bool CanInstall() => !IsBusy;

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
        CurrentFile = null;
        DownloadedBytesDisplay = "";
        StatusText = $"正在安装 {item.Name}…";
        Log.Information("开始安装 {Version}", item.Name);

        IProgress<LaunchProgress> uiProgress = new Progress<LaunchProgress>(p =>
        {
            if (p.TotalTasks > 0)
            {
                var percent = Math.Max(item.InstallPercent, p.Percent);
                item.InstallPercent = percent;
                ProgressPercent = percent;
            }
            CurrentFile = p.CurrentFile;
            if (p.TotalBytes > 0)
                DownloadedBytesDisplay =
                    $"{FormatBytes(p.ProgressedBytes)} / {FormatBytes(p.TotalBytes)}";
        });

        try
        {
            await GetService().InstallAsync(item.Name, uiProgress);
            item.IsInstalled = true;
            item.InstallPercent = 100;
            StatusText = $"{item.Name} 安装完成{_statusSuffix}";
            Log.Information("{Version} 安装完成", item.Name);
            RefreshInstalledList();
        }
        catch (Exception ex)
        {
            StatusText = $"安装 {item.Name} 失败:" + ex.Message;
            Log.Error(ex, "{Version} 安装失败", item.Name);
        }
        finally
        {
            item.IsInstalling = false;
            IsBusy = false;
            CurrentFile = null;
            ActiveDownloadName = null;
            DownloadedBytesDisplay = "";
        }
    }
}
