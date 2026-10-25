using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Daybreak.Core.Game;
using Daybreak.Core.Net;
using Daybreak.Core.Server;
using Serilog;

namespace Daybreak.Launcher.ViewModels;

/// <summary>
/// 联机页的「好友联机」部分(EasyTier 房间)。
///
/// 拆成 partial 文件是为了让主 VM 文件保持可读性 —— 这块逻辑与"我的服务端/服务器列表/局域网"
/// 相对独立,只是共用 <see cref="MultiplayerViewModel"/> 的状态栏与配置。
/// </summary>
public partial class MultiplayerViewModel
{
    private readonly EasyTierManager _easyTier = new();
    private readonly StringBuilder _roomLog = new();
    private readonly object _roomLogGate = new();

    public ObservableCollection<string> RoomGameVersions { get; } = [];

    /// <summary>用户输入的房间号(加入时填)。</summary>
    [ObservableProperty]
    private string _roomCodeInput = "";

    /// <summary>当前所在房间号(null = 不在房间)。</summary>
    [ObservableProperty]
    private string? _currentRoomCode;

    /// <summary>本机是否为房主。</summary>
    [ObservableProperty]
    private bool _roomIsHost;

    /// <summary>房间状态文案。</summary>
    [ObservableProperty]
    private string _roomStatusText = "未加入房间";

    /// <summary>已连接的好友数量。</summary>
    [ObservableProperty]
    private int _roomPeerCount;

    /// <summary>房间使用的游戏版本(用于提示双方保持一致)。</summary>
    [ObservableProperty]
    private string? _roomGameVersion;

    /// <summary>本机在虚拟网络里的 IP。</summary>
    [ObservableProperty]
    private string? _roomLocalIp;

    /// <summary>房间日志(用于排错)。</summary>
    [ObservableProperty]
    private string _roomLogText = "";

    /// <summary>联机组件是否就位(需要 easytier-core.exe + 中继地址两项都齐)。</summary>
    public bool IsRoomComponentReady => EasyTierManager.IsInstalled && EasyTierManager.IsRelayConfigured;

    /// <summary>是否已在房间中。</summary>
    public bool IsInRoom => CurrentRoomCode is not null;

    /// <summary>房主要连的地址(好友在游戏里填这个)。</summary>
    public string RoomHostAddress => RoomCredentials.HostIp + ":25565";

    /// <summary>组件/配置缺失时的提示(界面直接展示,避免用户困惑)。</summary>
    public string RoomComponentHint
    {
        get
        {
            if (!EasyTierManager.IsInstalled)
            {
                return $"联机组件缺失:未找到 {EasyTierManager.CorePath}\n" +
                       "请重新安装启动器(发布包内已包含 easytier 文件夹)。";
            }

            if (!EasyTierManager.IsRelayConfigured)
            {
                return $"中继节点未配置:未找到 {Path.Combine(EasyTierManager.BinaryDirectory, "relay.txt")}\n" +
                       "该文件应包含一行 host:port。请重新安装启动器,或手动创建该文件。";
            }

            return "";
        }
    }

    // 声明变更通知(供 XAML DataTrigger 使用)
    partial void OnCurrentRoomCodeChanged(string? value)
    {
        OnPropertyChanged(nameof(IsInRoom));
        OnPropertyChanged(nameof(ShowRoomSetup));
    }

    partial void OnRoomLocalIpChanged(string? value) => OnPropertyChanged(nameof(RoomHostAddress));

    public bool ShowRoomSetup => !IsInRoom;

    /// <summary>初始化房间相关(在构造器里调用一次)。</summary>
    private void InitializeRoomFeature()
    {
        _easyTier.LogReceived += line => AppendRoomLog(line);
        _easyTier.StateChanged += state =>
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                RoomStatusText = state switch
                {
                    EasyTierState.Starting => "正在连接中继节点…",
                    EasyTierState.WaitingForPeer => RoomIsHost
                        ? "已就绪,把房间号告诉好友"
                        : "已连上,等待房主开始",
                    EasyTierState.Connected => "已连接",
                    EasyTierState.Failed => "连接失败,请查看日志",
                    _ => "未加入房间",
                };

                // 状态变化时同步好友数量
                RoomPeerCount = Math.Max(0, _easyTier.PeerCount - 1); // 减掉中继本身
                if (_easyTier.LocalVirtualIp is not null)
                    RoomLocalIp = _easyTier.LocalVirtualIp;
            });
        };
    }

    private void AppendRoomLog(string line)
    {
        lock (_roomLogGate)
        {
            _roomLog.AppendLine(line);
            // 只保留尾部,避免长时间运行后无限增长
            if (_roomLog.Length > 60_000)
                _roomLog.Remove(0, _roomLog.Length - 40_000);
        }
    }

    /// <summary>把缓冲的日志刷到界面(由主 VM 的 300ms 定时器调用)。</summary>
    private void FlushRoomLog()
    {
        string text;
        lock (_roomLogGate)
        {
            if (_roomLog.Length == 0)
                return;
            text = _roomLog.ToString();
        }

        RoomLogText = text;
    }

    /// <summary>确保有管理员权限(联机需要创建虚拟网卡)。返回 false 表示需要用户重启。</summary>
    private bool EnsureElevatedOrNotify()
    {
        if (Services.ElevationService.IsElevated)
            return true;

        var answer = MessageBox.Show(
            "好友联机需要管理员权限(用于创建虚拟网卡)。\n\n" +
            "点「是」后启动器会以管理员身份重新启动,你的其他设置不会丢失。",
            "需要管理员权限",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);

        if (answer != MessageBoxResult.Yes)
            return false;

        if (Services.ElevationService.TryRestartElevated())
        {
            // 新实例已拉起,当前实例退出
            System.Windows.Application.Current?.Shutdown();
            return false;
        }

        MessageBox.Show("提权被取消或失败,无法启用联机功能。", "提示",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    /// <summary>创建房间(本机为房主)。</summary>
    [RelayCommand]
    private async Task CreateRoomAsync()
    {
        if (IsBusy)
            return;

        if (!EnsureElevatedOrNotify())
            return;

        if (!IsRoomComponentReady)
        {
            MessageBox.Show(RoomComponentHint, "联机组件缺失",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var version = SelectedRoomVersion;
        if (string.IsNullOrWhiteSpace(version))
        {
            MessageBox.Show("请先选择一个游戏版本,并确保好友使用相同版本。",
                "请选择版本", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        IsBusy = true;
        try
        {
            var code = RoomCode.Generate();
            var credentials = RoomCode.Derive(code);

            RoomGameVersion = version;
            RoomIsHost = true;
            RoomCodeInput = code;
            RoomStatusText = "正在启动联机组件…";
            RoomLogText = "";

            await _easyTier.StartAsync(credentials, isHost: true);

            CurrentRoomCode = code;
            RoomLocalIp = RoomCredentials.HostIp;
            StatusText = $"房间 {code} 已创建,把房间号发给好友";
            Log.Information("已创建联机房间 {Code}(版本 {Version})", code, version);
        }
        catch (Exception ex)
        {
            RoomStatusText = "创建失败";
            StatusText = $"创建房间失败:{ex.Message}";
            Log.Warning(ex, "创建联机房间失败");
            MessageBox.Show(ex.Message, "创建房间失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            await _easyTier.StopAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>加入房间(手输 6 位房间号)。</summary>
    [RelayCommand]
    private async Task JoinRoomAsync()
    {
        if (IsBusy)
            return;

        if (!EnsureElevatedOrNotify())
            return;

        if (!IsRoomComponentReady)
        {
            MessageBox.Show(RoomComponentHint, "联机组件缺失",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var code = RoomCode.Normalize(RoomCodeInput);
        if (code is null)
        {
            MessageBox.Show("房间号应为 6 位数字(如 123456)。",
                "房间号格式不对", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var version = SelectedRoomVersion;
        if (string.IsNullOrWhiteSpace(version))
        {
            MessageBox.Show("请先选择你正在使用的游戏版本(需与房主一致)。",
                "请选择版本", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        IsBusy = true;
        try
        {
            var credentials = RoomCode.Derive(code);

            RoomGameVersion = version;
            RoomIsHost = false;
            RoomStatusText = "正在加入房间…";
            RoomLogText = "";

            await _easyTier.StartAsync(credentials, isHost: false);

            CurrentRoomCode = code;
            RoomLocalIp = _easyTier.LocalVirtualIp;
            StatusText = $"已加入房间 {code}";
            Log.Information("已加入联机房间 {Code}(版本 {Version})", code, version);
        }
        catch (Exception ex)
        {
            RoomStatusText = "加入失败";
            StatusText = $"加入房间失败:{ex.Message}";
            Log.Warning(ex, "加入联机房间失败");
            MessageBox.Show(ex.Message, "加入房间失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            await _easyTier.StopAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>结束房间。</summary>
    [RelayCommand]
    private async Task LeaveRoomAsync()
    {
        await _easyTier.StopAsync();

        CurrentRoomCode = null;
        RoomIsHost = false;
        RoomPeerCount = 0;
        RoomLocalIp = null;
        RoomStatusText = "未加入房间";
        StatusText = "已结束联机";

        Log.Information("已结束联机房间");
    }

    /// <summary>复制房间号到剪贴板。</summary>
    [RelayCommand]
    private void CopyRoomCode()
    {
        if (string.IsNullOrEmpty(CurrentRoomCode))
            return;

        try
        {
            Clipboard.SetText(CurrentRoomCode);
            StatusText = $"房间号 {CurrentRoomCode} 已复制";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "复制房间号失败");
        }
    }

    /// <summary>
    /// 一键进服:启动游戏并直连房主的虚拟 IP。
    /// 用 --server/--port 直连,用户不用在游戏里手动输地址。
    /// </summary>
    [RelayCommand]
    private async Task JoinRoomGameAsync()
    {
        if (IsBusy || CurrentRoomCode is null)
            return;

        var versionId = SelectedRoomVersion;
        if (string.IsNullOrWhiteSpace(versionId))
        {
            MessageBox.Show("请先选择游戏版本。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (RoomIsHost && !IsServerRunningForVersion(versionId))
        {
            var proceed = MessageBox.Show(
                $"还没有检测到运行中的游戏服务端(端口 25565)。\n\n" +
                $"请先在「我的服务端」里启动一个 {versionId} 的服务端,否则好友进不来。\n\n" +
                "仍然继续启动游戏吗?",
                "服务端可能未运行",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (proceed != MessageBoxResult.Yes)
                return;
        }

        IsBusy = true;
        try
        {
            var launchService = GetLaunchService();
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
                // 直连房主的虚拟 IP
                ExtraGameArguments = ["--server", RoomCredentials.HostIp, "--port", "25565"],
            };

            StatusText = $"正在启动游戏并进入房间 {CurrentRoomCode}…";

            await Task.Run(() => launchService.LaunchAsync(
                request,
                new Progress<LaunchProgress>(p =>
                {
                    if (!string.IsNullOrEmpty(p.CurrentFile))
                        StatusText = p.CurrentFile;
                }),
                null,
                null));

            StatusText = "游戏已启动,正在进入房间";
            Log.Information("房间内一键进服:{Version}", versionId);
        }
        catch (Exception ex)
        {
            StatusText = $"进服失败:{ex.Message}";
            Log.Warning(ex, "房间内一键进服失败");
            MessageBox.Show(ex.Message, "进服失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>房主是否已经开了对应版本的服(粗略判断:端口被占用)。</summary>
    private static bool IsServerRunningForVersion(string versionId)
    {
        // 不校验版本是否匹配(需要读服务端配置),只看端口有没有在监听
        return ServerManager.IsPortInUse(25565);
    }

    /// <summary>刷新可选游戏版本(来自本机已安装版本)。</summary>
    private void RefreshRoomGameVersions()
    {
        RoomGameVersions.Clear();
        try
        {
            foreach (var v in GetLaunchService().GetInstalledVersions())
                RoomGameVersions.Add(v);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "读取已安装版本失败");
        }

        if (SelectedRoomVersion is null && RoomGameVersions.Count > 0)
            SelectedRoomVersion = RoomGameVersions[0];
    }

    [ObservableProperty]
    private string? _selectedRoomVersion;

    /// <summary>应用退出时清理联机组件。</summary>
    private async Task ShutdownRoomAsync()
    {
        try
        {
            await _easyTier.StopAsync();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "退出时清理联机组件失败");
        }
    }
}