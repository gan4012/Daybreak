using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Daybreak.Core.Game;
using Daybreak.Core.Market;
using Daybreak.Core.Mod;
using Daybreak.Core.Storage;
using Serilog;

namespace Daybreak.Launcher.ViewModels;

/// <summary>搜索结果项,携带自己的安装状态。</summary>
public partial class MarketResultItem : ObservableObject
{
    public required ModrinthSearchResult Result { get; init; }

    [ObservableProperty]
    private bool _isInstalling;

    [ObservableProperty]
    private int _installPercent;

    public string Title => Result.Title;
    public string Author => Result.Author;
    public string Description => Result.Description;
    public string DownloadsDisplay => Result.Downloads switch
    {
        >= 1_000_000 => $"{Result.Downloads / 1_000_000.0:F1}M",
        >= 1_000 => $"{Result.Downloads / 1_000.0:F0}K",
        _ => Result.Downloads.ToString(),
    };
}

public partial class ModsViewModel : ObservableObject
{
    private readonly LauncherConfig _config = LauncherConfig.Load();
    private LaunchService? _launchService;
    private ModInstallService? _installService;

    private readonly ModrinthClient _client = new();

    public ObservableCollection<string> TypeModes { get; } = ["Mod", "资源包", "光影", "整合包"];
    public ObservableCollection<string> InstalledVersions { get; } = [];
    public ObservableCollection<MarketResultItem> Results { get; } = [];

    [ObservableProperty]
    private int _selectedTypeIndex;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string? _selectedTargetVersion;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "选择目标版本后搜索资源";

    [ObservableProperty]
    private int _progressPercent;

    [ObservableProperty]
    private string? _activeDownloadName;

    [ObservableProperty]
    private string? _currentFile;

    [ObservableProperty]
    private string _downloadedBytesDisplay = "";

    private bool _versionsLoaded;

    public ModsViewModel() => _ = LoadVersionsAsync();

    partial void OnIsBusyChanged(bool value)
    {
        SearchCommand.NotifyCanExecuteChanged();
        InstallCommand.NotifyCanExecuteChanged();
    }

    private LaunchService GetService() =>
        _launchService ??= new LaunchService(_config.GameDirectory, _config.Mirror, _config.VersionIsolation);

    private ModInstallService GetInstallService() =>
        _installService ??= new ModInstallService(GetService(), _config.Mirror);

    private async Task LoadVersionsAsync()
    {
        try
        {
            var versions = await Task.Run(() => GetService().GetInstalledVersions());
            InstalledVersions.Clear();
            foreach (var version in versions)
                InstalledVersions.Add(version);

            SelectedTargetVersion ??= versions.FirstOrDefault();
            StatusText = InstalledVersions.Count == 0
                ? "还没有已安装的版本,先去「下载游戏」页获取"
                : $"资源将安装到:{SelectedTargetVersion}";
            _versionsLoaded = true;
        }
        catch (Exception ex)
        {
            StatusText = "读取版本列表失败:" + ex.Message;
            Log.Warning(ex, "资源市场版本列表读取失败");
        }
    }

    /// <summary>从版本名解析游戏版本与加载器(如 1.20.1-fabric-0.20.1 → 1.20.1 + fabric)。</summary>
    private (string GameVersion, string? Loader) ParseVersionName(string versionName)
    {
        foreach (var loader in new[] { "fabric", "neoforge", "forge" })
        {
            var marker = $"-{loader}";
            var index = versionName.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index > 0)
                return (versionName[..index], loader);
        }
        return (versionName, null);
    }

    private bool CanSearch() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchAsync()
    {
        if (SelectedTargetVersion is null)
        {
            StatusText = "请先安装并选择一个游戏版本";
            return;
        }

        IsBusy = true;
        StatusText = "正在搜索…";
        try
        {
            var (gameVersion, loader) = ParseVersionName(SelectedTargetVersion);
            var projectType = SelectedTypeIndex switch
            {
                1 => "resourcepack",
                2 => "shader",
                3 => "modpack",
                _ => "mod",
            };
            // 整合包与游戏版本强相关,保留过滤;其余类型也按版本过滤便于选择兼容资源
            var page = await _client.SearchAsync(
                string.IsNullOrWhiteSpace(SearchText) ? "" : SearchText.Trim(),
                projectType, gameVersion,
                projectType == "mod" ? loader : null);

            Results.Clear();
            foreach (var hit in page.Hits)
                Results.Add(new MarketResultItem { Result = hit });

            StatusText = $"找到 {page.TotalHits} 个结果(显示前 {Results.Count})";
        }
        catch (Exception ex)
        {
            StatusText = "搜索失败:" + ex.Message;
            Log.Warning(ex, "Modrinth 搜索失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanInstall() => !IsBusy && SelectedTargetVersion is not null;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync(MarketResultItem? item)
    {
        if (item is null || SelectedTargetVersion is null || item.IsInstalling)
            return;

        IsBusy = true;
        item.IsInstalling = true;
        item.InstallPercent = 0;
        ProgressPercent = 0;
        ActiveDownloadName = item.Title;
        CurrentFile = null;
        DownloadedBytesDisplay = "";
        StatusText = $"正在安装 {item.Title}…";

        IProgress<LaunchProgress> uiProgress = new Progress<LaunchProgress>(p =>
        {
            if (p.TotalTasks > 0 && p.TotalBytes > 0)
            {
                var percent = (int)Math.Clamp(p.ProgressedBytes * 100.0 / p.TotalBytes, 0, 100);
                item.InstallPercent = Math.Max(item.InstallPercent, percent);
                ProgressPercent = percent;
            }
            CurrentFile = p.CurrentFile;
            if (p.TotalBytes > 0)
                DownloadedBytesDisplay =
                    $"{FormatBytes(p.ProgressedBytes)} / {FormatBytes(p.TotalBytes)}";
        });

        try
        {
            var (gameVersion, loader) = ParseVersionName(SelectedTargetVersion);

            if (item.Result.ProjectType == "modpack")
            {
                // 整合包:下载 mrpack 到临时目录再导入
                var versions = await _client.GetVersionsAsync(item.Result.ProjectId, gameVersion, null);
                var packVersion = versions.FirstOrDefault()
                    ?? throw new InvalidOperationException("没有兼容的整合包版本");
                var packFile = packVersion.PrimaryFile
                    ?? throw new InvalidOperationException("整合包没有可下载文件");

                StatusText = $"下载整合包 {packFile.Filename}…";
                var tempPack = Path.Combine(Path.GetTempPath(), "Daybreak", "packs", packFile.Filename);
                Directory.CreateDirectory(Path.GetDirectoryName(tempPack)!);
                var engine = new DownloadEngine(_config.Mirror);
                await engine.DownloadFileAsync(
                    new HttpClient(), packFile.Url, tempPack, packFile.Size, packFile.Sha1);

                StatusText = "导入整合包…";
                await GetInstallService().ImportModrinthPackAsync(
                    SelectedTargetVersion, tempPack, uiProgress);
                File.Delete(tempPack);
            }
            else
            {
                var versions = await _client.GetVersionsAsync(
                    item.Result.ProjectId, gameVersion, loader);
                var version = versions.FirstOrDefault()
                    ?? throw new InvalidOperationException($"没有兼容 {gameVersion}({loader ?? "原版"})的版本");

                StatusText = $"下载 {version.VersionNumber}…";
                await GetInstallService().InstallModAsync(SelectedTargetVersion, version, uiProgress);
            }

            StatusText = $"{item.Title} 安装完成";
            Log.Information("资源安装完成:{Title} → {Version}", item.Title, SelectedTargetVersion);
        }
        catch (Exception ex)
        {
            StatusText = $"安装 {item.Title} 失败:" + ex.Message;
            Log.Warning(ex, "资源安装失败:{Title}", item.Title);
        }
        finally
        {
            item.IsInstalling = false;
            IsBusy = false;
            ActiveDownloadName = null;
            CurrentFile = null;
            DownloadedBytesDisplay = "";
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F2} GB",
        >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
        _ => $"{bytes / 1_024.0:F0} KB",
    };
}
