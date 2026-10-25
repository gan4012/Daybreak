using System.Diagnostics;
using System.IO;
using System.Text;

namespace Daybreak.Core.Net;

/// <summary>联机连接状态。</summary>
public enum EasyTierState
{
    /// <summary>未启动。</summary>
    Idle,

    /// <summary>正在启动组件。</summary>
    Starting,

    /// <summary>已连上中继，但还没有对端(等好友进房间)。</summary>
    WaitingForPeer,

    /// <summary>已有对端连接，可以开始游戏。</summary>
    Connected,

    /// <summary>启动失败。</summary>
    Failed,
}

/// <summary>
/// EasyTier 虚拟组网进程管理。
///
/// 工作方式:
/// - 双方连到同一个自建中继节点(地址从 easytier/relay.txt 读取,不进 git)。
/// - 同一个房间号派生出同一个 network-name / network-secret，EasyTier 据此把双方放进同一虚拟网络。
/// - 房主固定虚拟 IP <see cref="RoomCredentials.HostIp"/>(10.144.144.1)，加入者用 --dhcp 自动分配。
/// - 游戏直连 10.144.144.1:25565 即可。
///
/// **需要管理员权限**:EasyTier 创建虚拟网卡(TUN)必须有管理员，所以启动器整体以管理员身份运行
/// (见 app.manifest)。这样也能正常结束本进程，不会留下孤儿虚拟网卡。
/// </summary>
public class EasyTierManager : IDisposable
{
    /// <summary>
    /// 中继节点地址(host:port)。
    ///
    /// **不硬编码在源码里** —— 这个仓库是公开的,把自建节点的 IP 写进源码等于公示服务器地址,
    /// 容易被扫描/滥用。改为从随包分发的 easytier/relay.txt 读取(该文件不进 git)。
    /// 未配置时联机功能会明确提示,而不是静默连到某个地址。
    /// </summary>
    public static string? RelayEndpoint
    {
        get
        {
            try
            {
                var path = Path.Combine(BinaryDirectory, "relay.txt");
                if (!File.Exists(path))
                    return null;

                var text = File.ReadAllText(path).Trim();
                if (text.Length == 0 || text.StartsWith('#'))
                    return null;

                // 只取第一行有效内容，忽略注释
                var line = text.Split('\n')
                    .Select(l => l.Trim())
                    .FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));
                return string.IsNullOrWhiteSpace(line) ? null : line;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>中继地址是否已配置。</summary>
    public static bool IsRelayConfigured => RelayEndpoint is not null;

    private readonly object _gate = new();
    private Process? _process;
    private bool _disposed;

    /// <summary>保留最近若干行输出，失败时放进异常里，避免用户只看到一句"启动失败"。</summary>
    private readonly Queue<string> _outputTail = new();
    private const int OutputTailSize = 25;

    public EasyTierState State { get; private set; } = EasyTierState.Idle;

    /// <summary>当前所在房间(未进房间时为 null)。</summary>
    public string? RoomCode { get; private set; }

    /// <summary>本机是否为房主。</summary>
    public bool IsHost { get; private set; }

    /// <summary>本机在虚拟网络中的 IP(加入者要等 EasyTier 分配后才知道)。</summary>
    public string? LocalVirtualIp { get; private set; }

    /// <summary>已连接的对端数量。</summary>
    public int PeerCount { get; private set; }

    /// <summary>EasyTier 输出一行(已去掉行尾)。调用方负责节流。</summary>
    public event Action<string>? LogReceived;

    /// <summary>状态变化。</summary>
    public event Action<EasyTierState>? StateChanged;

    // ===== 二进制定位 =====

    /// <summary>EasyTier 组件目录(随启动器一起分发)。</summary>
    public static string BinaryDirectory => Path.Combine(AppContext.BaseDirectory, "easytier");

    public static string CorePath => Path.Combine(BinaryDirectory, "easytier-core.exe");

    /// <summary>组件是否就位。缺失时界面要给出可操作的提示，而不是静默失败。</summary>
    public static bool IsInstalled => File.Exists(CorePath);

    /// <summary>TUN 所需的随附文件是否齐全(wintun 是创建虚拟网卡的关键)。</summary>
    public static bool HasTunSupport =>
        File.Exists(Path.Combine(BinaryDirectory, "wintun.dll")) ||
        File.Exists(Path.Combine(BinaryDirectory, "WinDivert64.sys"));

    // ===== 启停 =====

    /// <summary>
    /// 构造 easytier-core 的命令行参数(纯函数，便于单元测试)。
    ///
    /// 两个实测得来的硬约束:
    /// 1. **只能有一个 -e**:传两个(即便协议不同)easytier-core 会立刻退出且不写日志;
    /// 2. 加入者用 --dhcp 而非固定 IP:多人进同一房间时固定 IP 会冲突。
    /// </summary>
    /// <param name="relayEndpoint">中继节点 host:port(来自 relay.txt,不进 git)</param>
    public static List<string> BuildArguments(RoomCredentials credentials, bool isHost, string relayEndpoint)
    {
        if (string.IsNullOrWhiteSpace(relayEndpoint))
            throw new ArgumentException("中继节点地址不能为空", nameof(relayEndpoint));

        var args = new List<string>();

        // 虚拟 IP:房主固定(好友要连它)，加入者交给 EasyTier 自动分配
        if (isHost)
        {
            args.Add("--ipv4");
            args.Add(RoomCredentials.HostIp);
        }
        else
        {
            args.Add("--dhcp");
        }

        args.Add("--network-name");
        args.Add(credentials.NetworkName);
        args.Add("--network-secret");
        args.Add(credentials.NetworkSecret);

        // 只给一个中继入口(见上面的实测约束)
        args.Add("-e");
        args.Add($"tcp://{relayEndpoint}");

        return args;
    }

    /// <summary>
    /// 进入房间。
    /// 房主用固定 IP，加入者用 --dhcp 让 EasyTier 自动分配，避免多人进同一房间时 IP 冲突。
    /// </summary>
    public async Task StartAsync(
        RoomCredentials credentials,
        bool isHost,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(EasyTierManager));

        if (!IsInstalled)
            throw new InvalidOperationException(
                $"联机组件缺失。请确认安装目录下有 easytier 文件夹(应包含 easytier-core.exe)。\n" +
                $"当前位置:{BinaryDirectory}");

        await StopAsync().ConfigureAwait(false);

        RoomCode = credentials.RoomCode;
        IsHost = isHost;
        LocalVirtualIp = isHost ? RoomCredentials.HostIp : null;
        PeerCount = 0;
        SetState(EasyTierState.Starting);

        var relay = RelayEndpoint
            ?? throw new InvalidOperationException(
                "未配置联机中继节点。请确认安装目录下的 easytier\\relay.txt 存在且内容为一行 host:port。");

        var args = BuildArguments(credentials, isHost, relay);

        if (!HasTunSupport)
        {
            // 没有 wintun/WinDivert 时组网能连上但本机拿不到虚拟网卡，游戏无法直连虚拟 IP。
            // 明确报错，避免用户面对"连上了却进不去"的困惑。
            throw new InvalidOperationException(
                "缺少 wintun.dll / WinDivert64.sys，无法创建虚拟网卡，联机不可用。\n" +
                "请重新安装启动器(发布包内已包含这两个文件)。");
        }

        var psi = new ProcessStartInfo
        {
            FileName = CorePath,
            WorkingDirectory = BinaryDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => HandleLine(e.Data);
        process.ErrorDataReceived += (_, e) => HandleLine(e.Data);
        process.Exited += (_, _) => OnExited();

        if (!process.Start())
            throw new InvalidOperationException("无法启动联机组件(easytier-core.exe)");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        lock (_gate)
            _process = process;

        // 给 EasyTier 一点时间建立连接，再判断是否连上了中继
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(6), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 用户取消不算错误
        }

        if (process.HasExited)
        {
            SetState(EasyTierState.Failed);

            var tail = GetOutputTail();
            throw new InvalidOperationException(
                "联机组件启动后立即退出。\n\n" +
                $"启动参数:{string.Join(' ', args)}\n\n" +
                $"最近输出:\n{tail}\n\n" +
                "最常见原因是**没有管理员权限**(无法创建虚拟网卡)。");
        }

        if (State == EasyTierState.Starting)
            SetState(EasyTierState.WaitingForPeer);
    }

    /// <summary>离开房间(结束进程)。</summary>
    public Task StopAsync()
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
        }

        if (process is not null)
        {
            try
            {
                process.OutputDataReceived -= (_, e) => HandleLine(e.Data);
                process.ErrorDataReceived -= (_, e) => HandleLine(e.Data);

                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch (Exception)
            {
                // 进程可能刚好自己退出了
            }
            finally
            {
                process.Dispose();
            }
        }

        RoomCode = null;
        IsHost = false;
        LocalVirtualIp = null;
        PeerCount = 0;
        SetState(EasyTierState.Idle);
        return Task.CompletedTask;
    }

    // ===== 日志解析 =====

    /// <summary>
    /// 从 EasyTier 输出里提取连接状态。
    ///
    /// 关键行(实测):
    /// - <c>new peer added peer_id=123</c> —— 新对端加入
    /// - <c>connect to peer error</c> —— 连中继失败
    /// - <c>tun device error</c> / <c>Failed to create adapter</c> —— 权限不足
    /// - <c>ipv4 = "10.x.x.x"</c> —— 自己的虚拟 IP
    /// </summary>
    private void HandleLine(string? line)
    {
        if (string.IsNullOrEmpty(line))
            return;

        lock (_gate)
        {
            _outputTail.Enqueue(line);
            while (_outputTail.Count > OutputTailSize)
                _outputTail.Dequeue();
        }

        LogReceived?.Invoke(line);

        if (line.Contains("new peer added", StringComparison.Ordinal))
        {
            PeerCount++;
            SetState(EasyTierState.Connected);
            return;
        }

        // 自己分配的虚拟 IP(dhcp 模式下需要知道它，界面上展示)
        if (LocalVirtualIp is null && line.Contains("ipv4", StringComparison.Ordinal))
        {
            var match = System.Text.RegularExpressions.Regex.Match(line, @"(?<!\d)(\d{1,3}(?:\.\d{1,3}){3})(?!\d)");
            if (match.Success && match.Groups[1].Value.StartsWith("10.", StringComparison.Ordinal))
                LocalVirtualIp = match.Groups[1].Value;
        }

        if (line.Contains("tun device error", StringComparison.Ordinal) ||
            line.Contains("Failed to create adapter", StringComparison.Ordinal))
        {
            SetState(EasyTierState.Failed);
            return;
        }

        if (line.Contains("connect to peer error", StringComparison.Ordinal) &&
            State == EasyTierState.Starting)
        {
            SetState(EasyTierState.Failed);
        }
    }

    private void OnExited()
    {
        if (State is not (EasyTierState.Idle or EasyTierState.Failed))
            SetState(EasyTierState.Failed);
    }

    /// <summary>取最近输出(拼成多行),用于失败诊断。</summary>
    private string GetOutputTail()
    {
        lock (_gate)
        {
            if (_outputTail.Count == 0)
                return "(无输出)";

            return string.Join('\n', _outputTail.Select(l =>
                l.Length > 200 ? l[..200] + "…" : l));
        }
    }

    private void SetState(EasyTierState state)
    {
        if (State == state)
            return;

        State = state;
        StateChanged?.Invoke(state);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // 退出流程里的清理失败不阻塞
        }
    }
}