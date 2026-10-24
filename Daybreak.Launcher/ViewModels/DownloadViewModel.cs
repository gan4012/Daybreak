using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CmlLib.Core.VersionMetadata;
using Daybreak.Core.Game;
using Daybreak.Core.Storage;
using Serilog;

namespace Daybreak.Launcher.ViewModels;

/// <summary>在线版本列表项。</summary>
public class OnlineVersionItem
{
    public required IVersionMetadata Metadata { get; init; }
    public bool IsInstalled { get; set; }

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

    /// <summary>false = 只显示正式版。</summary>
    [ObservableProperty]
    private bool _showAll;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "点击刷新获取版本列表";

    /// <summary>0 = 不确定进度。</summary>
    [ObservableProperty]
    private int _progressPercent;

    [ObservableProperty]
    private string? _currentFile;

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
                .Select(m => new OnlineVersionItem { Metadata = m, IsInstalled = installed.Contains(m.Name) })
                .ToList();

            ApplyFilter();
            StatusText = $"共 {_allVersions.Count} 个版本{_config.Mirror switch { "bmclapi" => "(BMCLAPI 镜像)", _ => "(官方源)" }}";
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

    private bool CanInstall() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync(OnlineVersionItem? item)
    {
        if (item is null || item.IsInstalled)
            return;

        IsBusy = true;
        ProgressPercent = 0;
        CurrentFile = null;
        StatusText = $"正在安装 {item.Name}…";
        Log.Information("开始安装 {Version}", item.Name);

        var uiProgress = new Progress<LaunchProgress>(p =>
        {
            if (p.TotalTasks > 0)
                ProgressPercent = Math.Max(ProgressPercent, p.Percent);
            CurrentFile = p.CurrentFile;
        });

        try
        {
            await GetService().InstallAsync(item.Name, uiProgress);
            item.IsInstalled = true;
            StatusText = $"{item.Name} 安装完成";
            Log.Information("{Version} 安装完成", item.Name);
        }
        catch (Exception ex)
        {
            StatusText = "安装失败:" + ex.Message;
            Log.Error(ex, "{Version} 安装失败", item.Name);
        }
        finally
        {
            IsBusy = false;
            CurrentFile = null;
        }
    }
}
