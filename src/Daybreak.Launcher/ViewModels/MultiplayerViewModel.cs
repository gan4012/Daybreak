using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Daybreak.Core.Game;
using Daybreak.Core.Server;
using Daybreak.Core.Storage;
using Serilog;

namespace Daybreak.Launcher.ViewModels;

/// <summary>
/// 服务端列表里的一项(含运行状态与日志缓冲)。
/// 日志量远大于游戏安装,所以这里只保留尾部若干行,并且由 VM 层做节流刷新。
/// </summary>
public partial class ServerItemViewModel : ObservableObject
{
    /// <summary>控制台最多保留的行数(再多没有阅读价值,还吃内存)。</summary>
    private const int MaxConsoleLines = 500;

    private readonly StringBuilder _pendingLog = new();
    private readonly object _logGate = new();

    public ServerInstance Instance { get; }

    public ServerItemViewModel(ServerInstance instance) => Instance = instance;

    public string Name => Instance.Name;
    public string TypeDisplay => Instance.TypeDisplay;
    public string VersionDisplay => Instance.VersionDisplay;
    public int Port => Instance.Port;

    [ObservableProperty]
    private ServerState _state = ServerState.Stopped;

    [ObservableProperty]
    private string _stateText = "已停止";

    [ObservableProperty]
    private string _consoleText = "";

    [ObservableProperty]
    private bool _eulaAccepted;

    /// <summary>是否需要提示用户同意 EULA(未同意时显示提示条)。</summary>
    public bool NeedsEula => !EulaAccepted;

    partial void OnEulaAcceptedChanged(bool value) => OnPropertyChanged(nameof(NeedsEula));

    /// <summary>端口是否被占用(启动前预检结果的展示)。</summary>
    [ObservableProperty]
    private bool _portInUse;

    /// <summary>在线人数(服务端运行时用 SLP 查自己)。</summary>
    [ObservableProperty]
    private string _playerDisplay = "—";

    public bool IsRunning => State is ServerState.Running or ServerState.Starting;
    public bool IsStopped => State == ServerState.Stopped;

    /// <summary>状态指示灯颜色。</summary>
    public string StateColor => State switch
    {
        ServerState.Running => "#7ACC7A",
        ServerState.Starting or ServerState.Stopping => "#E8C27A",
        _ => "#8A8A8A",
    };

    /// <summary>当前已订阅的进程(防止重复挂接导致日志打印多份)。</summary>
    private ServerProcess? _attachedProcess;

    /// <summary>
    /// 把该项挂到服务端进程上。**幂等**:已经挂过同一个进程时返回 false,
    /// 避免切页面回来时重复订阅造成日志重复。
    /// </summary>
    public bool AttachTo(ServerProcess process)
    {
        if (ReferenceEquals(_attachedProcess, process))
            return false;

        _attachedProcess = process;

        process.StateChanged += e =>
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                State = e.State;
                StateText = e.Message ?? DescribeState(e.State);
            });
        };

        return true;
    }

    private static string DescribeState(ServerState state) => state switch
    {
        ServerState.Running => "运行中",
        ServerState.Starting => "启动中…",
        ServerState.Stopping => "停止中…",
        _ => "已停止",
    };

    /// <summary>缓冲一行日志。调用方必须自己节流(不要每行都触发 UI 刷新)。</summary>
    public void AppendLog(string line)
    {
        lock (_logGate)
        {
            _pendingLog.AppendLine(line);
        }
    }

    /// <summary>把缓冲的日志刷进 ConsoleText(由节流定时器调用)。</summary>
    public void FlushLog()
    {
        string pending;
        lock (_logGate)
        {
            if (_pendingLog.Length == 0)
                return;

            pending = _pendingLog.ToString();
            _pendingLog.Clear();
        }

        var combined = ConsoleText + pending;
        var lines = combined.Split('\n');

        // 只保留尾部,避免长时间运行后字符串无限增长导致 UI 卡顿
        if (lines.Length > MaxConsoleLines)
            combined = string.Join('\n', lines[^MaxConsoleLines..]);

        ConsoleText = combined;
    }

    public void ClearLog()
    {
        lock (_logGate)
            _pendingLog.Clear();
        ConsoleText = "";
    }

    partial void OnStateChanged(ServerState value)
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsStopped));
        OnPropertyChanged(nameof(StateColor));
    }
}

/// <summary>
/// 联机页 ViewModel:三块内容(我的服务端 / 服务器列表 / 局域网)合在一个页面里用 Tab 切换。
/// 拆成三个子 VM 是为了各自独立刷新,避免开服时把整个页面都搅动一遍。
/// </summary>
public partial class MultiplayerViewModel : ObservableObject
{
    private readonly LauncherConfig _config = LauncherConfig.Load();
    private LaunchService? _launchService;
    private ServerManager? _serverManager;
    private ServerListService? _serverListService;
    private LanScanner? _lanScanner;
    private System.Windows.Threading.DispatcherTimer? _logTimer;
    private System.Windows.Threading.DispatcherTimer? _lanTimer;

    public ObservableCollection<ServerItemViewModel> Servers { get; } = [];
    public ObservableCollection<SavedServerItemViewModel> SavedServers { get; } = [];
    public ObservableCollection<LanWorld> LanWorlds { get; } = [];

    /// <summary>是否已有服务端(空列表时显示引导文案)。</summary>
    public bool HasServers => Servers.Count > 0;

    /// <summary>是否已发现局域网房间。</summary>
    public bool HasLanWorlds => LanWorlds.Count > 0;

    /// <summary>可创建的服务端类型(展示名)。</summary>
    public ObservableCollection<string> ServerTypes { get; } =
        ["原版", "Forge", "Fabric", "Paper"];

    /// <summary>当前 Tab:0=我的服务端 1=服务器列表 2=局域网。</summary>
    [ObservableProperty]
    private int _selectedTabIndex;

    // 以下属性供 XAML 的 DataTrigger 使用(项目里没有 Converter,统一走 DataTrigger)
    public bool IsServersTab => SelectedTabIndex == 0;
    public bool IsListTab => SelectedTabIndex == 1;
    public bool IsLanTab => SelectedTabIndex == 2;
    public bool IsRoomTab => SelectedTabIndex == 3;

    partial void OnSelectedTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsServersTab));
        OnPropertyChanged(nameof(IsListTab));
        OnPropertyChanged(nameof(IsLanTab));
        OnPropertyChanged(nameof(IsRoomTab));
    }

    [ObservableProperty]
    private ServerItemViewModel? _selectedServer;

    /// <summary>是否选中了服务端(供 XAML 切换详情区与空状态提示)。</summary>
    public bool HasSelectedServer => SelectedServer is not null;

    partial void OnSelectedServerChanged(ServerItemViewModel? value) =>
        OnPropertyChanged(nameof(HasSelectedServer));

    [ObservableProperty]
    private SavedServerItemViewModel? _selectedSavedServer;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _isBusy;

    // ---- 新建服务端表单 ----

    [ObservableProperty]
    private string _newServerName = "";

    [ObservableProperty]
    private int _newServerTypeIndex;

    [ObservableProperty]
    private string _newServerVersion = "";

    [ObservableProperty]
    private string _newServerLoaderVersion = "";

    [ObservableProperty]
    private int _newServerPort = 25565;

    [ObservableProperty]
    private int _newServerMemoryMb = 2048;

    [ObservableProperty]
    private bool _newServerAcceptEula;

    /// <summary>可选游戏版本(按已选类型动态加载)。</summary>
    public ObservableCollection<string> AvailableVersions { get; } = [];

    /// <summary>可选加载器版本(Forge/Fabric)。</summary>
    public ObservableCollection<string> AvailableLoaderVersions { get; } = [];

    // ---- 新建服务器表单 ----

    [ObservableProperty]
    private string _newServerAddress = "";

    [ObservableProperty]
    private string _newServerAlias = "";

    /// <summary>控制台命令输入。</summary>
    [ObservableProperty]
    private string _consoleInput = "";

    public MultiplayerViewModel()
    {
        // 集合变化时通知对应的布尔属性(XAML 用 DataTrigger 判断空状态)
        Servers.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasServers));
        LanWorlds.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasLanWorlds));

        RefreshServers();
        RefreshSavedServers();
        StartLogTimer();

        // 好友联机(EasyTier 房间)初始化
        InitializeRoomFeature();
        RefreshRoomGameVersions();

        // 类型变更会重新拉版本列表,进入页面时先把当前类型的版本拉好
        _ = LoadVersionsForTypeAsync();
    }

    /// <summary>
    /// 每次进入联机页时调用。
    ///
    /// 单例 VM 下这一步很关键:页面切走期间服务端可能一直在跑,
    /// 回来时必须重新同步状态、重新挂事件,否则会显示成"已停止"。
    /// </summary>
    public void OnNavigatedTo()
    {
        EnsureManagerMatchesConfig();
        RefreshServers();

        // 服务端在跑但端口检测说被占用是正常的(就是它自己占的),
        // 这里重算一次,避免用旧快照把运行中的服务端标成"端口冲突"
        foreach (var item in Servers)
            item.PortInUse = !item.IsRunning && ServerManager.IsPortInUse(item.Port);

        StatusText = Servers.Count == 0
            ? "还没有服务端。点「新建服务端」创建一个,或去「服务器列表」添加别人的服务器。"
            : $"共 {Servers.Count} 个服务端" +
              (Servers.Any(s => s.IsRunning) ? $",{Servers.Count(s => s.IsRunning)} 个运行中" : "");
    }

    /// <summary>应用退出时统一收尾:停掉本启动器托管的所有服务端,避免留下孤儿 Java 进程。</summary>
    public async Task ShutdownAsync()
    {
        _logTimer?.Stop();
        _logTimer = null;

        try
        {
            if (_serverManager is not null)
                await _serverManager.StopAllAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "退出时停止服务端失败");
        }

        // 局域网监听也要一并释放,否则退出时会占着 UDP 4445
        StopLanScanIfRunning();

        // 联机组件(EasyTier)也要清理,否则会留下虚拟网卡
        await ShutdownRoomAsync();
    }

    private LaunchService GetLaunchService() =>
        _launchService ??= new LaunchService(_config.GameDirectory, _config.Mirror, _config.VersionIsolation);

    private ServerManager GetServerManager() =>
        _serverManager ??= new ServerManager(GetLaunchService(), _config.Mirror);

    private ServerListService GetServerListService() =>
        _serverListService ??= new ServerListService(GetLaunchService().ResolveGameDirectory());

    /// <summary>
    /// 日志节流:服务端刷屏比游戏还猛(尤其 Paper/Forge 启动期),
    /// 每行都刷新 UI 会直接卡死界面。
    /// </summary>
    private void StartLogTimer()
    {
        _logTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300),
        };
        _logTimer.Tick += (_, _) =>
        {
            foreach (var server in Servers)
                server.FlushLog();

            // 房间日志也走同一个节流器
            FlushRoomLog();
        };
        _logTimer.Start();
    }

    // ===== 我的服务端 =====

    [RelayCommand]
    private void RefreshServers()
    {
        var manager = GetServerManager();

        // 保留已有项:直接重建会丢掉控制台日志,而且会把事件重复订阅一遍
        // (重复订阅会导致同一行日志被打印多次)
        var existing = Servers.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var instances = manager.List();

        // 删掉已经不存在的
        for (var i = Servers.Count - 1; i >= 0; i--)
        {
            if (!instances.Any(x => x.Name.Equals(Servers[i].Name, StringComparison.OrdinalIgnoreCase)))
                Servers.RemoveAt(i);
        }

        for (var i = 0; i < instances.Count; i++)
        {
            var instance = instances[i];
            var state = manager.GetState(instance.Name);
            var eula = EulaService.IsAccepted(manager.ResolveInstanceDirectory(instance.Name));
            var portInUse = ServerManager.IsPortInUse(instance.Port);

            if (existing.TryGetValue(instance.Name, out var item))
            {
                // 复用旧项:状态与日志都留着,只刷新会变的环境信息
                item.State = state;
                item.StateText = DescribeState(state);
                item.EulaAccepted = eula;
                item.PortInUse = portInUse;

                var currentIndex = Servers.IndexOf(item);
                if (currentIndex != i)
                    Servers.Move(currentIndex, i);
            }
            else
            {
                item = new ServerItemViewModel(instance)
                {
                    State = state,
                    StateText = DescribeState(state),
                    EulaAccepted = eula,
                    PortInUse = portInUse,
                };

                if (i < Servers.Count)
                    Servers.Insert(i, item);
                else
                    Servers.Add(item);
            }
        }

        // 关键:回到本页时,仍在运行的服务端要重新挂上日志与状态事件,
        // 否则控制台会"空白"、状态也不会再更新(单例 VM 下进程是活的,只是没订阅)
        foreach (var item in Servers.Where(s => s.IsRunning))
            AttachProcess(item);
    }

    /// <summary>把服务端进程的日志/状态事件挂到 UI 项上(幂等:同一项只挂一次)。</summary>
    private void AttachProcess(ServerItemViewModel item)
    {
        var process = GetServerManager().GetProcess(item.Name);
        if (process is null)
            return;

        if (!item.AttachTo(process))
            return;

        process.OutputReceived += item.AppendLog;
    }

    /// <summary>确认服务端管理器仍指向当前配置(设置页改了游戏目录/镜像后要跟着换)。</summary>
    private void EnsureManagerMatchesConfig()
    {
        var current = LauncherConfig.Load();
        var gameDirChanged = !string.Equals(current.GameDirectory, _config.GameDirectory, StringComparison.OrdinalIgnoreCase);
        var mirrorChanged = !string.Equals(current.Mirror, _config.Mirror, StringComparison.OrdinalIgnoreCase);

        if (!gameDirChanged && !mirrorChanged)
            return;

        // 先停掉旧管理器托管的服务端,避免换目录后留下管不到的孤儿进程
        try
        {
            _serverManager?.StopAllAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "切换配置时停止旧服务端失败");
        }

        _config.GameDirectory = current.GameDirectory;
        _config.Mirror = current.Mirror;
        _launchService = null;
        _serverManager = null;
        _serverListService = null;
    }

    private static string DescribeState(ServerState state) => state switch
    {
        ServerState.Running => "运行中",
        ServerState.Starting => "启动中…",
        ServerState.Stopping => "停止中…",
        _ => "已停止",
    };

    [RelayCommand]
    private async Task StartServerAsync(ServerItemViewModel? item)
    {
        if (item is null || IsBusy)
            return;

        // 「已在运行」不是失败:快速连点按钮、或状态还没同步回来时会走到这里,
        // 报成"启动失败"会让用户以为出问题了(实测踩过)。
        if (GetServerManager().IsRunning(item.Name))
        {
            item.State = ServerState.Running;
            item.StateText = "运行中";
            StatusText = $"{item.Name} 已在运行中";
            return;
        }

        IsBusy = true;
        StatusText = $"正在启动 {item.Name}…";

        try
        {
            item.ClearLog();
            item.AppendLog($"[Daybreak] 正在启动 {item.Name}…");

            var progress = new Progress<LaunchProgress>(p =>
            {
                if (!string.IsNullOrEmpty(p.CurrentFile))
                    item.AppendLog($"[Daybreak] {p.CurrentFile}");
            });

            var process = await GetServerManager().StartAsync(item.Instance, progress);
            process.OutputReceived += item.AppendLog;
            process.StateChanged += e =>
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    item.State = e.State;
                    item.StateText = e.Message ?? DescribeState(e.State);

                    if (e.State == ServerState.Stopped)
                    {
                        // 异常退出要把退出码带出来,否则用户只看到"已停止"却不知为何
                        if (e.ExitCode is not null and not 0)
                            StatusText = $"{item.Name} 已退出(退出码 {e.ExitCode}),请查看控制台日志";

                        RefreshServers();
                    }
                });
            };

            item.State = ServerState.Running;
            item.StateText = "运行中";
            StatusText = $"{item.Name} 已启动";
            Log.Information("已启动服务端 {Name}", item.Name);
        }
        catch (Exception ex)
        {
            item.AppendLog($"[Daybreak] 启动失败:{ex.Message}");
            StatusText = $"启动失败:{ex.Message}";
            Log.Warning(ex, "启动服务端 {Name} 失败", item.Name);
            MessageBox.Show(ex.Message, "启动服务端失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task StopServerAsync(ServerItemViewModel? item)
    {
        if (item is null || IsBusy)
            return;

        IsBusy = true;
        StatusText = $"正在停止 {item.Name}(会先让服务端保存世界)…";

        try
        {
            await GetServerManager().StopAsync(item.Name);
            item.State = ServerState.Stopped;
            item.StateText = "已停止";
            StatusText = $"{item.Name} 已停止";
        }
        catch (Exception ex)
        {
            StatusText = $"停止失败:{ex.Message}";
            Log.Warning(ex, "停止服务端 {Name} 失败", item.Name);
        }
        finally
        {
            IsBusy = false;
            RefreshServers();
        }
    }

    /// <summary>发送控制台命令(op / whitelist / say 等)。</summary>
    [RelayCommand]
    private void SendConsoleCommand()
    {
        var item = SelectedServer;
        if (item is null || string.IsNullOrWhiteSpace(ConsoleInput))
            return;

        var command = ConsoleInput.Trim();
        ConsoleInput = "";

        if (!GetServerManager().SendCommand(item.Name, command))
        {
            item.AppendLog("[Daybreak] 命令发送失败:服务端未在运行");
            return;
        }

        item.AppendLog($"> {command}");
    }

    [RelayCommand]
    private void ClearConsole() => SelectedServer?.ClearLog();

    /// <summary>
    /// 同意 EULA。**必须由用户主动点击**——绝不在创建流程里默认勾选,
    /// 这是 Mojang EULA 的要求。
    /// </summary>
    [RelayCommand]
    private void AcceptEula(ServerItemViewModel? item)
    {
        if (item is null)
            return;

        var confirmed = MessageBox.Show(
            "同意 Minecraft 最终用户许可协议(EULA)?\n\n" +
            "点击「是」表示你已阅读并同意 https://aka.ms/MinecraftEULA 的条款。\n" +
            "服务端只有在同意后才能启动。",
            "确认同意 EULA",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirmed != MessageBoxResult.Yes)
            return;

        var dir = GetServerManager().ResolveInstanceDirectory(item.Name);
        EulaService.Accept(dir);
        item.EulaAccepted = true;
        StatusText = $"{item.Name} 已同意 EULA,可以启动";
    }

    [RelayCommand]
    private void OpenServerFolder(ServerItemViewModel? item)
    {
        if (item is null)
            return;

        var dir = GetServerManager().ResolveInstanceDirectory(item.Name);
        Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = dir,
            UseShellExecute = true,
        });
    }

    [RelayCommand]
    private async Task DeleteServerAsync(ServerItemViewModel? item)
    {
        if (item is null || IsBusy)
            return;

        if (item.IsRunning)
        {
            MessageBox.Show("请先停止服务端再删除。", "无法删除",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var choice = MessageBox.Show(
            $"删除服务端「{item.Name}」?\n\n" +
            "「是」= 连同世界存档一起删除(不可恢复)\n" +
            "「否」= 只从列表移除,保留文件",
            "删除服务端",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);

        if (choice == MessageBoxResult.Cancel)
            return;

        var deleteFiles = choice == MessageBoxResult.Yes;

        IsBusy = true;
        try
        {
            await GetServerManager().DeleteAsync(item.Name, deleteFiles);
            StatusText = deleteFiles
                ? $"{item.Name} 已删除(含文件)"
                : $"{item.Name} 已从列表移除(文件保留)";
            RefreshServers();
            SelectedServer = null;
        }
        catch (Exception ex)
        {
            StatusText = $"删除失败:{ex.Message}";
            MessageBox.Show(ex.Message, "删除失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ===== 新建服务端 =====

    /// <summary>切换服务端类型时重新拉取可用版本。</summary>
    partial void OnNewServerTypeIndexChanged(int value) => _ = LoadVersionsForTypeAsync();

    private string CurrentServerType => NewServerTypeIndex switch
    {
        1 => ServerType.Forge,
        2 => ServerType.Fabric,
        3 => ServerType.Paper,
        _ => ServerType.Vanilla,
    };

    [RelayCommand]
    private async Task LoadVersionsForTypeAsync()
    {
        AvailableVersions.Clear();
        AvailableLoaderVersions.Clear();
        NewServerVersion = "";

        try
        {
            IsBusy = true;
            StatusText = "正在获取可用版本…";

            var type = CurrentServerType;

            if (type == ServerType.Forge)
            {
                // Forge 的版本列表与客户端共用一个来源
                var loader = new Core.Mod.LoaderService(_config.GameDirectory, _config.Mirror);
                var versions = await Task.Run(() => loader.GetForgeVersionsAsync("1.20.1"));
                foreach (var v in versions.Take(50))
                    AvailableLoaderVersions.Add(v.Name);
                AvailableVersions.Add("1.20.1");
                NewServerVersion = "1.20.1";
                StatusText = $"已加载 {AvailableLoaderVersions.Count} 个 Forge 版本(游戏版本暂固定 1.20.1)";
                return;
            }

            var downloader = new ServerDownloader(_config.Mirror);
            var list = await downloader.GetVersionsAsync(type);
            foreach (var option in list.Take(80))
                AvailableVersions.Add(option.GameVersion);

            if (AvailableVersions.Count > 0)
                NewServerVersion = AvailableVersions[0];

            StatusText = $"已加载 {AvailableVersions.Count} 个可用版本";
        }
        catch (Exception ex)
        {
            StatusText = $"获取版本列表失败:{ex.Message}";
            Log.Warning(ex, "获取服务端版本列表失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LoadLoaderVersionsAsync()
    {
        AvailableLoaderVersions.Clear();

        if (string.IsNullOrWhiteSpace(NewServerVersion))
            return;

        try
        {
            var downloader = new ServerDownloader(_config.Mirror);

            if (CurrentServerType == ServerType.Fabric)
            {
                var loaders = await downloader.GetFabricLoaderVersionsAsync(NewServerVersion);
                foreach (var v in loaders.Take(30))
                    AvailableLoaderVersions.Add(v);
            }
            else if (CurrentServerType == ServerType.Paper)
            {
                var builds = await downloader.GetPaperBuildsAsync(NewServerVersion);
                foreach (var b in builds.Take(30))
                    AvailableLoaderVersions.Add(b);
            }

            if (AvailableLoaderVersions.Count > 0)
                NewServerLoaderVersion = AvailableLoaderVersions[0];
        }
        catch (Exception ex)
        {
            StatusText = $"获取加载器版本失败:{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CreateServerAsync()
    {
        if (IsBusy)
            return;

        // 合规:必须用户主动勾选同意 EULA,不默认勾
        if (!NewServerAcceptEula)
        {
            MessageBox.Show(
                "请先勾选同意 Minecraft EULA。\n\n服务端只有在同意后才会启动,这是 Mojang 的要求。",
                "需要同意 EULA",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(NewServerName))
        {
            MessageBox.Show("请填写服务端名称。", "信息不完整",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(NewServerVersion))
        {
            MessageBox.Show("请选择游戏版本。", "信息不完整",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var type = CurrentServerType;

        if (type is ServerType.Forge or ServerType.Fabric or ServerType.Paper &&
            string.IsNullOrWhiteSpace(NewServerLoaderVersion))
        {
            var what = type == ServerType.Paper ? "构建号" : "加载器版本";
            MessageBox.Show($"请选择{what}。", "信息不完整",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (ServerManager.IsPortInUse(NewServerPort))
        {
            var proceed = MessageBox.Show(
                $"端口 {NewServerPort} 当前已被占用。\n\n" +
                "如果那是别的程序在用,服务端将无法启动。仍要继续创建吗?",
                "端口被占用",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (proceed != MessageBoxResult.Yes)
                return;
        }

        IsBusy = true;
        try
        {
            var instance = new ServerInstance
            {
                Name = NewServerName.Trim(),
                Type = type,
                GameVersion = NewServerVersion,
                LoaderVersion = string.IsNullOrWhiteSpace(NewServerLoaderVersion) ? null : NewServerLoaderVersion,
                Port = NewServerPort,
                MaxMemoryMb = NewServerMemoryMb,
                MinMemoryMb = Math.Max(512, NewServerMemoryMb / 2),
                AcceptEula = true,
            };

            StatusText = $"正在创建 {instance.Name}(需要下载服务端核心,请稍候)…";

            var progress = new Progress<LaunchProgress>(p =>
            {
                if (!string.IsNullOrEmpty(p.CurrentFile))
                    StatusText = p.CurrentFile;
            });

            await GetServerManager().CreateAsync(instance, progress);

            // 用户在表单里已勾选同意,这里落到 eula.txt
            EulaService.Accept(GetServerManager().ResolveInstanceDirectory(instance.Name));

            StatusText = $"{instance.Name} 创建完成,可以启动";
            Log.Information("已创建服务端 {Name}({Type} {Version})", instance.Name, type, instance.GameVersion);

            // 重置表单
            NewServerName = "";
            NewServerAcceptEula = false;

            RefreshServers();
            SelectedTabIndex = 0;
        }
        catch (Exception ex)
        {
            StatusText = $"创建失败:{ex.Message}";
            Log.Warning(ex, "创建服务端失败");
            MessageBox.Show(ex.Message, "创建服务端失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ===== 服务器列表 =====

    [RelayCommand]
    private void RefreshSavedServers()
    {
        SavedServers.Clear();
        foreach (var server in GetServerListService().List())
        {
            var item = new SavedServerItemViewModel(server);
            item.InitializeFromServer();
            SavedServers.Add(item);
        }
    }

    [RelayCommand]
    private void AddSavedServer()
    {
        if (string.IsNullOrWhiteSpace(NewServerAddress))
        {
            MessageBox.Show("请填写服务器地址,例如 mc.example.com 或 1.2.3.4:25565。",
                "信息不完整", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var added = GetServerListService().Add(NewServerAddress, NewServerAlias);
            var item = new SavedServerItemViewModel(added);
            SavedServers.Add(item);

            NewServerAddress = "";
            NewServerAlias = "";
            StatusText = $"已添加 {added.DisplayName},正在查询状态…";

            // 添加后立刻查一次,用户不用再点「查询全部」
            _ = RefreshOneAsync(item);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "添加失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void RemoveSavedServer(SavedServerItemViewModel? item)
    {
        if (item is null)
            return;

        GetServerListService().Remove(item.Host, item.Port);
        SavedServers.Remove(item);
        StatusText = $"已移除 {item.DisplayName}";
    }

    [RelayCommand]
    private void ToggleFavorite(SavedServerItemViewModel? item)
    {
        if (item is null)
            return;

        item.Favorite = !item.Favorite;
        item.Server.Favorite = item.Favorite;
        GetServerListService().Update(item.Server);

        // 重排:收藏项优先
        var ordered = SavedServers
            .OrderByDescending(s => s.Favorite)
            .ThenBy(s => s.DisplayName, StringComparer.CurrentCulture)
            .ToList();

        SavedServers.Clear();
        foreach (var s in ordered)
            SavedServers.Add(s);

        StatusText = item.Favorite ? $"{item.DisplayName} 已收藏" : $"{item.DisplayName} 已取消收藏";
    }

    [RelayCommand]
    private async Task RefreshAllSavedServersAsync()
    {
        if (IsBusy || SavedServers.Count == 0)
            return;

        IsBusy = true;
        StatusText = $"正在查询 {SavedServers.Count} 个服务器…";

        try
        {
            foreach (var item in SavedServers)
                item.IsRefreshing = true;

            var done = 0;
            var online = 0;

            // 用被包装的 Core 对象去查询,回来后按引用找到对应的 UI 项更新
            var cores = SavedServers.Select(s => s.Server).ToList();

            await GetServerListService().RefreshAllAsync(
                cores,
                _ =>
                {
                    Interlocked.Increment(ref done);
                },
                TimeSpan.FromSeconds(6));

            // 查询完成后统一切回 UI 线程刷新属性(包装类有 INPC,赋值即可生效)
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                foreach (var item in SavedServers)
                {
                    item.ApplyStatus(item.Server.Status);
                    item.IsRefreshing = false;

                    if (item.IsOnline)
                        online++;
                }
            });

            StatusText = $"查询完成:{done} 个服务器,{online} 个在线";
        }
        catch (Exception ex)
        {
            StatusText = $"查询失败:{ex.Message}";
            Log.Warning(ex, "刷新服务器列表失败");

            foreach (var item in SavedServers)
                item.IsRefreshing = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshOneAsync(SavedServerItemViewModel item)
    {
        item.IsRefreshing = true;
        try
        {
            var status = await ServerListPing.QueryAsync(item.Host, item.Port, TimeSpan.FromSeconds(6));

            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                item.ApplyStatus(status);
                StatusText = status.Online
                    ? $"{item.DisplayName} 在线,{status.PlayerDisplay} 人"
                    : $"{item.DisplayName} 查询失败:{status.Error}";
            });
        }
        catch (Exception ex)
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                StatusText = $"{item.DisplayName} 查询失败:{ex.Message}");
        }
        finally
        {
            item.IsRefreshing = false;
        }
    }

    /// <summary>
    /// 一键进服:启动游戏并带上 --server/--port 直连。
    /// 需要一个已安装的游戏版本;没装则提示用户先去下载游戏。
    /// </summary>
    [RelayCommand]
    private async Task JoinServerAsync(SavedServerItemViewModel? item)
    {
        if (item is null || IsBusy)
            return;

        var server = item.Server;
        IsBusy = true;
        try
        {
            var launchService = GetLaunchService();
            var versions = await Task.Run(() => launchService.GetInstalledVersions());

            if (versions.Count == 0)
            {
                MessageBox.Show(
                    "还没有安装任何游戏版本。请先去「下载游戏」页装一个,再回来进服。",
                    "没有可用版本", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 选一个适合进服的版本:优先原版(带加载器的版本进普通服可能因 Mod 不匹配被踢)
            var versionId = versions.FirstOrDefault(v =>
                !v.Contains("forge", StringComparison.OrdinalIgnoreCase) &&
                !v.Contains("fabric", StringComparison.OrdinalIgnoreCase) &&
                !v.Contains("neoforge", StringComparison.OrdinalIgnoreCase))
                ?? versions[0];

            var proceed = MessageBox.Show(
                $"将启动游戏「{versionId}」并直接进入 {server.Address}。\n\n是否继续?",
                "一键进服", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (proceed != MessageBoxResult.Yes)
                return;

            // 取活动账号名;没有账号就用配置里的名字(离线)
            var accountStore = Core.Auth.AccountStore.Load();
            var playerName = accountStore.Find(accountStore.ActiveId)?.Name ?? _config.PlayerName;

            var request = new LaunchRequest(
                versionId,
                playerName,
                _config.GameDirectory,
                _config.Mirror,
                _config.MaxMemoryMb,
                _config.JavaDirectory)
            {
                JvmArguments = _config.JvmArguments,
                // 关键:交给游戏本体 --server/--port,由游戏自己做 SRV 解析
                ExtraGameArguments = ServerListService.BuildDirectConnectArguments(server),
            };

            StatusText = $"正在准备启动游戏并进入 {server.Address}…";

            var process = await Task.Run(() => launchService.LaunchAsync(
                request,
                new Progress<LaunchProgress>(p =>
                {
                    if (!string.IsNullOrEmpty(p.CurrentFile))
                        StatusText = p.CurrentFile;
                }),
                null,
                null));

            StatusText = $"游戏已启动,正在进入 {server.Address}";
            Log.Information("一键进服:{Address}(版本 {Version})", server.Address, versionId);
        }
        catch (Exception ex)
        {
            StatusText = $"进服失败:{ex.Message}";
            Log.Warning(ex, "一键进服失败");
            MessageBox.Show(ex.Message, "进服失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ===== 局域网 =====

    [ObservableProperty]
    private bool _isLanScanning;

    [RelayCommand]
    private void ToggleLanScan()
    {
        if (IsLanScanning)
        {
            StopLanScan();
            return;
        }

        try
        {
            _lanScanner = new LanScanner();
            _lanScanner.WorldDiscovered += world =>
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    if (LanWorlds.All(w => w.Address != world.Address || w.Port != world.Port))
                        LanWorlds.Add(world);
                });
            };

            _lanScanner.Start();
            IsLanScanning = true;
            StatusText = "正在监听局域网广播(在游戏里点「对局域网开放」后会自动出现)";

            // 定时清理已关闭的房间
            _lanTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2),
            };
            _lanTimer.Tick += (_, _) =>
            {
                var alive = _lanScanner?.GetAliveWorlds() ?? [];
                for (var i = LanWorlds.Count - 1; i >= 0; i--)
                {
                    var world = LanWorlds[i];
                    if (!alive.Any(a => a.Address == world.Address && a.Port == world.Port))
                        LanWorlds.RemoveAt(i);
                }
            };
            _lanTimer.Start();
        }
        catch (Exception ex)
        {
            // 4445 被占用等情况:广播发现不可用,提示用户改用服务器列表直连
            IsLanScanning = false;
            StatusText = "无法监听局域网广播(端口 4445 可能被占用或被防火墙拦截)";
            Log.Warning(ex, "启动局域网监听失败");
        }
    }

    private void StopLanScan()
    {
        _lanTimer?.Stop();
        _lanTimer = null;

        _lanScanner?.Stop();
        _lanScanner?.Dispose();
        _lanScanner = null;

        IsLanScanning = false;
        LanWorlds.Clear();
        StatusText = "已停止局域网扫描";
    }

    [RelayCommand]
    private async Task ScanLanByPortAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;
        StatusText = "正在扫描本机网段的 25565 端口…";

        try
        {
            var progress = new Progress<int>(p => StatusText = $"正在扫描本机网段…{p}%");

            var found = await LanScanner.ScanSubnetAsync(
                25565,
                progress: progress);

            LanWorlds.Clear();
            foreach (var world in found)
                LanWorlds.Add(world);

            StatusText = found.Count == 0
                ? "网段扫描没找到开放的房间(广播发现更准确,建议用上面那个按钮)"
                : $"网段扫描找到 {found.Count} 个疑似房间";
        }
        catch (Exception ex)
        {
            StatusText = $"扫描失败:{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void JoinLanWorld(LanWorld? world)
    {
        if (world is null)
            return;

        // 局域网房间包一层 SavedServer,复用统一的一键进服逻辑
        var temp = new SavedServerItemViewModel(new SavedServer
        {
            Host = world.Address,
            Port = world.Port,
            Name = world.Motd,
        });

        _ = JoinServerAsync(temp);
    }

    /// <summary>
    /// 局域网监听交由用户手动开关(见 ToggleLanScan),**不随页面卸载自动停**——
    /// 之前那样做会在切页面时把还在用的监听一起停掉。
    /// 应用退出时由 ShutdownAsync 统一收尾。
    /// </summary>
    public void StopLanScanIfRunning()
    {
        if (IsLanScanning)
            StopLanScan();
    }
}