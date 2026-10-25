using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Daybreak.Core.Game;
using Daybreak.Core.Market;
using Daybreak.Core.Mod;
using Daybreak.Core.Storage;
using Serilog;

namespace Daybreak.Launcher.ViewModels;

/// <summary>版本内的 Mod 文件(含更新检查结果与一键更新状态)。</summary>
public partial class ModFileInfoItem : ObservableObject
{
    public required string Filename { get; init; }
    public required string SizeDisplay { get; init; }
    public required string Sha1 { get; init; }

    /// <summary>文件字节数(排序用;SizeDisplay 是给人看的字符串,无法比较)。</summary>
    public long SizeBytes { get; init; }

    [ObservableProperty]
    private string? _updateStatus;

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private bool _isUpdating;

    /// <summary>是否有可更新的新版本(有才显示「更新」按钮)。</summary>
    [ObservableProperty]
    private bool _canUpdate;

    /// <summary>远端可更新的版本;已是最新或非 Modrinth 资源时为 null。</summary>
    public ModrinthVersion? RemoteVersion { get; private set; }

    public void SetUpdateInfo(ModrinthVersion? remote)
    {
        if (remote is null)
        {
            RemoteVersion = null;
            CanUpdate = false;
            UpdateStatus = "非 Modrinth 资源";
            return;
        }

        var isLatest = Filename.Contains(remote.VersionNumber, StringComparison.OrdinalIgnoreCase);
        RemoteVersion = isLatest ? null : remote;
        CanUpdate = !isLatest;
        UpdateStatus = isLatest ? "已是最新" : $"可更新 → {remote.VersionNumber}";
    }
}

public partial class VersionsViewModel : ObservableObject
{
    private readonly LauncherConfig _config = LauncherConfig.Load();
    private LaunchService? _launchService;
    private ModInstallService? _installService;

    /// <summary>当前版本的完整 Mod 列表(Mods 是它经过搜索/排序后的视图)。</summary>
    private readonly List<ModFileInfoItem> _allMods = [];

    public ObservableCollection<InstalledVersionInfo> Versions { get; } = [];
    public ObservableCollection<ModFileInfoItem> Mods { get; } = [];

    /// <summary>Mod 列表排序方式。</summary>
    public ObservableCollection<string> ModSortModes { get; } = ["名称", "大小", "更新状态"];

    [ObservableProperty]
    private string _modSearchText = "";

    [ObservableProperty]
    private int _selectedModSortIndex;

    [ObservableProperty]
    private InstalledVersionInfo? _selectedVersion;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string _selectedVersionInfo = "";

    /// <summary>Mod 列表标题:带搜索时的计数提示。</summary>
    [ObservableProperty]
    private string _modListSummary = "";

    public VersionsViewModel() => _ = RefreshAsync();

    partial void OnSelectedVersionChanged(InstalledVersionInfo? value)
    {
        UpdateDetail();
        _ = LoadModsAsync();
    }

    partial void OnModSearchTextChanged(string value) => ApplyModFilter();

    partial void OnSelectedModSortIndexChanged(int value) => ApplyModFilter();

    /// <summary>按当前搜索词与排序方式重建 Mods 视图。</summary>
    private void ApplyModFilter()
    {
        var keyword = ModSearchText.Trim();

        IEnumerable<ModFileInfoItem> query = _allMods;
        if (keyword.Length > 0)
        {
            query = query.Where(m => m.Filename.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        query = SelectedModSortIndex switch
        {
            // 大小:从大到小(找大体积 Mod 时有用)
            1 => query.OrderByDescending(m => m.SizeBytes).ThenBy(m => m.Filename, StringComparer.OrdinalIgnoreCase),
            // 更新状态:可更新的排前面,便于批量处理
            2 => query.OrderByDescending(m => m.CanUpdate)
                      .ThenBy(m => m.Filename, StringComparer.OrdinalIgnoreCase),
            // 名称(默认)
            _ => query.OrderBy(m => m.Filename, StringComparer.OrdinalIgnoreCase),
        };

        Mods.Clear();
        foreach (var mod in query)
            Mods.Add(mod);

        ModListSummary = keyword.Length > 0
            ? $"Mod 列表({Mods.Count} / {_allMods.Count},筛选「{keyword}」)"
            : $"Mod 列表({_allMods.Count})";
    }

    private LaunchService GetService() =>
        _launchService ??= new LaunchService(_config.GameDirectory, _config.Mirror, _config.VersionIsolation);

    private ModInstallService GetInstallService() =>
        _installService ??= new ModInstallService(GetService(), _config.Mirror);

    [RelayCommand]
    private async Task RefreshAsync()
    {
        Log.Information("版本管理:开始刷新");
        IsBusy = true;
        try
        {
            var versions = await Task.Run(() => GetService().GetInstalledVersionInfos());
            Log.Information("版本管理:扫描完成 {Count} 个", versions.Count);
            var current = SelectedVersion?.Name;
            Versions.Clear();
            foreach (var version in versions)
                Versions.Add(version);
            SelectedVersion = versions.FirstOrDefault(v => v.Name == current) ?? versions.FirstOrDefault();
            StatusText = $"共 {versions.Count} 个版本";
            Log.Information("版本管理:{Count} 个版本", versions.Count);
        }
        catch (Exception ex)
        {
            StatusText = "读取版本列表失败:" + ex.Message;
            Log.Error(ex, "版本管理读取失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateDetail()
    {
        if (SelectedVersion is null)
        {
            SelectedVersionInfo = "";
            return;
        }

        var (gameVersion, loader) = ParseVersionName(SelectedVersion.Name);
        SelectedVersionInfo =
            $"游戏 {gameVersion} · {SizeDisplay(SelectedVersion.SizeBytes)} · " +
            (loader is null ? "原版" : loader);
    }

    [RelayCommand]
    private async Task LoadModsAsync()
    {
        _allMods.Clear();
        Mods.Clear();
        ModListSummary = "";

        if (SelectedVersion is null)
            return;

        var modsDir = GetService().ResolveModsDirectory(SelectedVersion.Name);
        var mods = await Task.Run(() => GetService().GetMods(SelectedVersion.Name)
            .Select(m =>
            {
                var sha1 = "";
                try
                {
                    using var stream = File.OpenRead(Path.Combine(modsDir, m.Name));
                    sha1 = Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(stream));
                }
                catch (IOException) { }
                return (Mod: m, Sha1: sha1);
            })
            .ToList());

        foreach (var (mod, sha1) in mods)
        {
            _allMods.Add(new ModFileInfoItem
            {
                Filename = mod.Name,
                SizeDisplay = SizeDisplay(mod.SizeBytes),
                SizeBytes = mod.SizeBytes,
                Sha1 = sha1,
            });
        }

        ApplyModFilter();
    }

    [RelayCommand]
    private void OpenVersionFolder()
    {
        if (SelectedVersion is null)
            return;
        var dir = Path.Combine(GetService().ResolveGameDirectory(), "versions", SelectedVersion.Name);
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
    }

    [RelayCommand]
    private async Task DeleteVersionAsync()
    {
        if (SelectedVersion is null || IsBusy)
            return;

        var name = SelectedVersion.Name;
        var confirm = System.Windows.MessageBox.Show(
            $"确定删除版本「{name}」及其存档吗?此操作不可恢复。",
            "删除版本",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (confirm != System.Windows.MessageBoxResult.Yes)
            return;

        IsBusy = true;
        try
        {
            await Task.Run(() => GetService().DeleteVersion(name));
            Log.Information("已删除版本 {Version}", name);
            await RefreshAsync();
            StatusText = $"已删除 {name}";
        }
        catch (Exception ex)
        {
            StatusText = "删除失败:" + ex.Message;
            Log.Error(ex, "删除版本失败 {Version}", name);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RenameVersionAsync()
    {
        if (SelectedVersion is null || IsBusy)
            return;

        var oldName = SelectedVersion.Name;
        var newName = ShowInputDialog($"重命名版本\n当前: {oldName}\n\n请输入新版本名:", "重命名版本", oldName);

        if (string.IsNullOrWhiteSpace(newName))
            return;

        // 清理非法文件名字符
        foreach (var c in Path.GetInvalidFileNameChars())
            newName = newName.Replace(c, '_');

        IsBusy = true;
        try
        {
            await Task.Run(() => GetService().RenameVersion(oldName, newName));
            Log.Information("已重命名版本 {Old} → {New}", oldName, newName);
            await RefreshAsync();
            StatusText = $"已重命名为 {newName}";
        }
        catch (Exception ex)
        {
            StatusText = "重命名失败:" + ex.Message;
            Log.Error(ex, "重命名版本失败 {Old} → {New}", oldName, newName);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CopyVersionAsync()
    {
        if (SelectedVersion is null || IsBusy)
            return;

        var sourceName = SelectedVersion.Name;
        var defaultName = $"{sourceName}-copy";
        var newName = ShowInputDialog($"复制版本\n源: {sourceName}\n\n请输入新版本名:", "复制版本", defaultName);

        if (string.IsNullOrWhiteSpace(newName))
            return;

        foreach (var c in Path.GetInvalidFileNameChars())
            newName = newName.Replace(c, '_');

        IsBusy = true;
        try
        {
            await Task.Run(() => GetService().CopyVersion(sourceName, newName));
            Log.Information("已复制版本 {Source} → {Target}", sourceName, newName);
            await RefreshAsync();
            StatusText = $"已复制为 {newName}";
        }
        catch (Exception ex)
        {
            StatusText = "复制失败:" + ex.Message;
            Log.Error(ex, "复制版本失败 {Source} → {Target}", sourceName, newName);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>显示一个简单的输入对话框(纯 WPF,无额外依赖)。</summary>
    private static string? ShowInputDialog(string prompt, string title, string defaultValue)
    {
        var window = new System.Windows.Window
        {
            Title = title,
            Width = 420,
            Height = 160,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
            Owner = App.Current?.MainWindow,
            ResizeMode = System.Windows.ResizeMode.NoResize,
        };

        var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(20) };

        var textBlock = new System.Windows.Controls.TextBlock
        {
            Text = prompt,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 10),
        };
        panel.Children.Add(textBlock);

        var textBox = new System.Windows.Controls.TextBox
        {
            Text = defaultValue,
            MinHeight = 30,
            Margin = new System.Windows.Thickness(0, 0, 0, 10),
        };
        panel.Children.Add(textBox);

        var grid = new System.Windows.Controls.Grid();
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });
        grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto });

        var okBtn = new System.Windows.Controls.Button { Content = "确定", MinWidth = 70, Margin = new System.Windows.Thickness(0, 0, 5, 0) };
        var cancelBtn = new System.Windows.Controls.Button { Content = "取消", MinWidth = 70 };

        System.Windows.Controls.Grid.SetColumn(okBtn, 0);
        System.Windows.Controls.Grid.SetColumn(cancelBtn, 1);
        grid.Children.Add(okBtn);
        grid.Children.Add(cancelBtn);
        panel.Children.Add(grid);

        window.Content = panel;

        string? result = null;
        bool cancelled = true;

        okBtn.Click += (_, _) => { cancelled = false; result = textBox.Text; window.Close(); };
        cancelBtn.Click += (_, _) => { cancelled = true; window.Close(); };

        window.ShowDialog();
        return cancelled ? null : result;
    }

    [RelayCommand]
    private async Task DeleteModAsync(ModFileInfoItem? mod)
    {
        if (mod is null || SelectedVersion is null)
            return;

        var modsDir = GetService().ResolveModsDirectory(SelectedVersion.Name);
        var path = Path.Combine(modsDir, mod.Filename);
        if (File.Exists(path))
        {
            File.Delete(path);
            Log.Information("已删除 Mod {Mod}", mod.Filename);
        }
        await LoadModsAsync();
        StatusText = $"已删除 {mod.Filename}";
    }

    /// <summary>一键更新单个 Mod:下载远端最新版本 → 删除旧文件 → 刷新列表。</summary>
    [RelayCommand]
    private async Task UpdateModAsync(ModFileInfoItem? mod)
    {
        if (mod is null || SelectedVersion is null || IsBusy)
            return;

        if (mod.RemoteVersion is null)
        {
            StatusText = $"{mod.Filename} 没有可用的更新版本(请先检查更新)";
            return;
        }

        var versionId = SelectedVersion.Name;
        var newVersion = mod.RemoteVersion.VersionNumber;
        var fileName = mod.Filename;

        IsBusy = true;
        StatusText = $"正在更新 {fileName} → {newVersion}…";
        try
        {
            if (await TryUpdateModAsync(versionId, mod))
            {
                StatusText = $"{fileName} 已更新到 {newVersion}";
                await LoadModsAsync();
            }
            else
            {
                StatusText = $"更新 {fileName} 失败,详见日志";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>执行一次 Mod 更新,返回是否成功。不修改 IsBusy(由调用方控制)。</summary>
    private async Task<bool> TryUpdateModAsync(string versionId, ModFileInfoItem mod)
    {
        var remote = mod.RemoteVersion;
        if (remote is null)
            return false;

        var oldFileName = mod.Filename;
        mod.IsUpdating = true;
        try
        {
            var modsDir = GetService().ResolveModsDirectory(versionId);
            var newPath = await GetInstallService().InstallModAsync(versionId, remote);

            // 文件名变化的 Mod(绝大多数把版本号写进文件名)必须删掉旧文件,否则会同时存在两个版本
            var oldPath = Path.Combine(modsDir, oldFileName);
            if (File.Exists(oldPath) &&
                !string.Equals(Path.GetFullPath(oldPath), Path.GetFullPath(newPath), StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Delete(oldPath);
                    Log.Information("已移除旧版本文件 {Old}", oldFileName);
                }
                catch (IOException ex)
                {
                    // 旧文件删不掉不影响新版本生效,但要让用户知道可能重复
                    Log.Warning(ex, "旧版本文件删除失败 {Old}", oldFileName);
                }
            }

            Log.Information("Mod 更新成功 {Old} → {Version}", oldFileName, remote.VersionNumber);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Mod 更新失败 {Mod}", oldFileName);
            return false;
        }
        finally
        {
            mod.IsUpdating = false;
        }
    }

    /// <summary>一键更新所有已检测出可更新的 Mod。</summary>
    [RelayCommand]
    private async Task UpdateAllModsAsync()
    {
        if (SelectedVersion is null || IsBusy)
            return;

        var pending = _allMods.Where(m => m.CanUpdate && m.RemoteVersion is not null).ToList();
        if (pending.Count == 0)
        {
            StatusText = "没有可更新的 Mod(请先点「检查 Mod 更新」)";
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            $"将更新 {pending.Count} 个 Mod(旧版本文件会被删除),是否继续?",
            "批量更新 Mod",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (confirm != System.Windows.MessageBoxResult.Yes)
            return;

        var versionId = SelectedVersion.Name;
        var succeeded = 0;
        var failed = 0;

        IsBusy = true;
        try
        {
            for (var i = 0; i < pending.Count; i++)
            {
                var mod = pending[i];
                StatusText = $"批量更新中…({i + 1}/{pending.Count}) {mod.Filename}";
                if (await TryUpdateModAsync(versionId, mod))
                    succeeded++;
                else
                    failed++;
            }

            StatusText = $"批量更新完成:成功 {succeeded} 个,失败 {failed} 个";
            Log.Information("批量更新 Mod 完成:成功 {Ok},失败 {Fail}", succeeded, failed);
            await LoadModsAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CheckModUpdatesAsync()
    {
        Log.Information("检查更新:开始({Mods} 个 Mod)", Mods.Count);
        if (SelectedVersion is null || IsBusy)
            return;

        IsBusy = true;
        StatusText = "正在检查 Mod 更新…";
        try
        {
            var client = new ModrinthClient();
            var installService = GetInstallService();
            var records = installService.GetInstallRecords(SelectedVersion.Name)
                .ToDictionary(r => r.Filename, StringComparer.OrdinalIgnoreCase);
            var outdated = 0;
            var checkedCount = 0;

            // 对完整列表做检查(不受搜索筛选影响),避免用户筛了词就漏检
            foreach (var mod in _allMods)
            {
                // 有安装记录的用项目 ID 精确查询;没有的回退 SHA1 反查
                if (records.TryGetValue(mod.Filename, out var record))
                {
                    mod.IsChecking = true;
                    var projectVersions = await client.GetVersionsAsync(record.ProjectId);
                    var remote = projectVersions
                        .OrderByDescending(v => v.DatePublished)
                        .FirstOrDefault();
                    mod.SetUpdateInfo(remote);
                    mod.IsChecking = false;

                    if (mod.CanUpdate)
                        outdated++;
                    checkedCount++;
                }
                else if (!string.IsNullOrEmpty(mod.Sha1))
                {
                    mod.IsChecking = true;
                    var remote = await client.GetVersionBySha1Async(mod.Sha1);
                    mod.SetUpdateInfo(remote);
                    mod.IsChecking = false;

                    if (mod.CanUpdate)
                        outdated++;
                    checkedCount++;
                }
            }

            // 更新状态变化后重排(排序方式选「更新状态」时能立刻把可更新项顶上来)
            ApplyModFilter();

            StatusText = checkedCount == 0
                ? "没有可检查的 Mod(仅支持 Modrinth 资源)"
                : $"检查完成:{checkedCount} 个 Mod 中 {outdated} 个有更新";
        }
        catch (Exception ex)
        {
            StatusText = "更新检查失败:" + ex.Message;
            Log.Warning(ex, "Mod 更新检查失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static (string GameVersion, string? Loader) ParseVersionName(string versionName)
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

    private static string SizeDisplay(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F2} GB",
        >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
        _ => $"{bytes / 1_024.0:F0} KB",
    };
}
