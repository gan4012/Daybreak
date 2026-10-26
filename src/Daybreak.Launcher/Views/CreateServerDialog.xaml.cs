using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using Daybreak.Core.Game;
using Daybreak.Core.Mod;
using Daybreak.Core.Server;
using Daybreak.Core.Storage;
using Serilog;
using Wpf.Ui.Controls;

namespace Daybreak.Launcher.Views;

/// <summary>
/// 新建服务端对话框。
///
/// 做成独立对话框(而不是塞进联机页)的原因:
/// 创建过程要下载几十到几百 MB 的服务端核心,耗时较长且需要连续的表单状态,
/// 放在页面里容易和列表刷新互相干扰。
/// </summary>
public partial class CreateServerDialog : FluentWindow
{
    private readonly LauncherConfig _config = LauncherConfig.Load();
    private bool _loading;

    /// <summary>是否真的创建成功(供调用方决定要不要刷新列表)。</summary>
    public bool Created { get; private set; }

    public CreateServerDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => _ = LoadVersionsAsync();
    }

    private string CurrentType => TypeCombo.SelectedIndex switch
    {
        1 => ServerType.Forge,
        2 => ServerType.Fabric,
        3 => ServerType.Paper,
        _ => ServerType.Vanilla,
    };

    private async void OnTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
            return;

        // 除 Paper 外都需要选加载器/构建版本,Paper 也需要(构建号)
        LoaderPanel.Visibility = CurrentType == ServerType.Vanilla
            ? Visibility.Collapsed
            : Visibility.Visible;

        LoaderLabel.Text = CurrentType switch
        {
            ServerType.Paper => "Paper 构建号",
            ServerType.Fabric => "Fabric 加载器版本",
            ServerType.Forge => "Forge 版本",
            _ => "加载器版本",
        };

        await LoadVersionsAsync();
    }

    private async void OnReloadVersions(object sender, RoutedEventArgs e) => await LoadVersionsAsync();

    private async void OnVersionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;

        await LoadLoaderVersionsAsync();
    }

    private async Task LoadVersionsAsync()
    {
        _loading = true;
        try
        {
            VersionCombo.Items.Clear();
            LoaderCombo.Items.Clear();
            StatusText.Text = "正在获取可用版本…";

            var type = CurrentType;

            if (type == ServerType.Forge)
            {
                // Forge 服务端安装流程在 1.13+ 才统一,这里固定用 1.20.1 保证成功率
                VersionCombo.Items.Add("1.20.1");
                VersionCombo.SelectedIndex = 0;

                var loader = new LoaderService(_config.GameDirectory, _config.Mirror);
                var versions = await Task.Run(() => loader.GetForgeVersionsAsync("1.20.1"));

                foreach (var v in versions.Take(60))
                    LoaderCombo.Items.Add(v.Name);

                if (LoaderCombo.Items.Count > 0)
                    LoaderCombo.SelectedIndex = 0;

                StatusText.Text = $"已加载 {LoaderCombo.Items.Count} 个 Forge 版本(游戏版本固定 1.20.1)";
                return;
            }

            var downloader = new ServerDownloader(_config.Mirror);
            var list = await downloader.GetVersionsAsync(type);

            foreach (var option in list.Take(100))
                VersionCombo.Items.Add(option.GameVersion);

            if (VersionCombo.Items.Count > 0)
                VersionCombo.SelectedIndex = 0;

            StatusText.Text = $"已加载 {VersionCombo.Items.Count} 个可用游戏版本";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"获取版本列表失败:{ex.Message}";
            Log.Warning(ex, "获取服务端版本列表失败");
        }
        finally
        {
            _loading = false;

            // 版本选好后立刻拉加载器列表
            await LoadLoaderVersionsAsync();
        }
    }

    private async Task LoadLoaderVersionsAsync()
    {
        LoaderCombo.Items.Clear();

        var gameVersion = VersionCombo.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(gameVersion) || CurrentType == ServerType.Vanilla)
            return;

        try
        {
            var downloader = new ServerDownloader(_config.Mirror);

            if (CurrentType == ServerType.Fabric)
            {
                var loaders = await downloader.GetFabricLoaderVersionsAsync(gameVersion);
                foreach (var v in loaders.Take(40))
                    LoaderCombo.Items.Add(v);
            }
            else if (CurrentType == ServerType.Paper)
            {
                var builds = await downloader.GetPaperBuildsAsync(gameVersion);
                foreach (var b in builds.Take(40))
                    LoaderCombo.Items.Add(b);
            }

            if (LoaderCombo.Items.Count > 0)
                LoaderCombo.SelectedIndex = 0;
            else if (CurrentType is ServerType.Fabric or ServerType.Paper)
                StatusText.Text = $"{ServerType.DisplayName(CurrentType)} 没有适配 {gameVersion} 的版本,请换一个游戏版本";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"获取加载器版本失败:{ex.Message}";
        }
    }

    private void OnOpenEula(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true,
            });
            e.Handled = true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "打开 EULA 链接失败");
        }
    }

    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var gameVersion = VersionCombo.SelectedItem as string;
        var loaderVersion = LoaderCombo.SelectedItem as string;
        var type = CurrentType;

        // ---- 表单校验 ----
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusText.Text = "请填写服务端名称。";
            return;
        }

        try
        {
            ServerManager.ValidateName(name);
        }
        catch (InvalidOperationException ex)
        {
            StatusText.Text = ex.Message;
            return;
        }

        if (string.IsNullOrWhiteSpace(gameVersion))
        {
            StatusText.Text = "请选择游戏版本。";
            return;
        }

        if (type != ServerType.Vanilla && string.IsNullOrWhiteSpace(loaderVersion))
        {
            StatusText.Text = type == ServerType.Paper ? "请选择 Paper 构建号。" : "请选择加载器版本。";
            return;
        }

        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1024 or > 65535)
        {
            StatusText.Text = "端口需在 1024 ~ 65535 之间(1024 以下是系统保留端口)。";
            return;
        }

        if (!int.TryParse(MemoryBox.Text.Trim(), out var memory) || memory is < 512 or > 32768)
        {
            StatusText.Text = "内存需在 512 ~ 32768 MB 之间。";
            return;
        }

        // ---- 合规:必须用户主动勾选 ----
        if (EulaCheck.IsChecked != true)
        {
            StatusText.Text = "请先勾选同意 Minecraft EULA,否则服务端无法启动。";
            return;
        }

        // ---- 端口占用提醒(不阻止创建,但要让用户知道)----
        if (ServerManager.IsPortInUse(port))
        {
            // 显式限定 System.Windows:Wpf.Ui.Controls 里也有同名的 MessageBox
            var proceed = System.Windows.MessageBox.Show(
                $"端口 {port} 当前已被占用,服务端可能无法启动。\n\n仍要继续创建吗?",
                "端口被占用",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);

            if (proceed != System.Windows.MessageBoxResult.Yes)
                return;
        }

        // ---- 创建 ----
        CreateButton.IsEnabled = false;

        try
        {
            var launchService = new LaunchService(
                _config.GameDirectory, _config.Mirror, _config.VersionIsolation);
            var manager = new ServerManager(launchService, _config.Mirror);

            var instance = new ServerInstance
            {
                Name = name,
                Type = type,
                GameVersion = gameVersion,
                LoaderVersion = string.IsNullOrWhiteSpace(loaderVersion) ? null : loaderVersion,
                Port = port,
                MaxMemoryMb = memory,
                MinMemoryMb = Math.Max(512, memory / 2),
                AcceptEula = true,
            };

            var progress = new Progress<LaunchProgress>(p =>
            {
                if (!string.IsNullOrEmpty(p.CurrentFile))
                    StatusText.Text = p.CurrentFile;
            });

            StatusText.Text = "正在下载并安装服务端核心(可能需要几分钟)…";
            await manager.CreateAsync(instance, progress);

            // 用户已勾选同意,写入 eula.txt
            EulaService.Accept(manager.ResolveInstanceDirectory(name));

            Log.Information("已创建服务端 {Name}({Type} {Version})", name, type, gameVersion);

            Created = true;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"创建失败:{ex.Message}";
            Log.Warning(ex, "创建服务端失败");
        }
        finally
        {
            CreateButton.IsEnabled = true;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}