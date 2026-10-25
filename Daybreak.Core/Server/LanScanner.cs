using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Daybreak.Core.Server;

/// <summary>发现到的局域网世界。</summary>
public record LanWorld(string Motd, int Port, string Address, DateTime LastSeen)
{
    /// <summary>该世界是否仍在广播(超过 5 秒没收到就认为关了)。</summary>
    public bool IsAlive => DateTime.Now - LastSeen < TimeSpan.FromSeconds(5);

    public string Display => $"{Motd}({Address}:{Port})";
}

/// <summary>
/// 局域网房间探测。
///
/// 主要手段是**监听 Minecraft 自己的局域网广播**:
/// 玩家在游戏里点「对局域网开放」后,客户端会周期性地向组播地址 224.0.2.60:4445
/// 发送形如 `[MOTD]房间名[/MOTD][AD]端口[/AD]` 的报文。
/// 这比端口扫描准确得多(能直接拿到房间名与端口,且不会误报普通服务端)。
///
/// 兜底手段是**网段端口扫描**,用于广播被防火墙拦住的情况——
/// 但扫描只能知道"某端口开着",拿不到房间名,所以只作为补充。
/// </summary>
public class LanScanner : IDisposable
{
    /// <summary>Minecraft 局域网广播使用的组播地址与端口。</summary>
    public static readonly IPAddress MulticastAddress = IPAddress.Parse("224.0.2.60");
    public const int MulticastPort = 4445;

    private readonly ConcurrentDictionary<string, LanWorld> _worlds = new();
    private readonly object _gate = new();

    private UdpClient? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private bool _disposed;

    /// <summary>发现新世界(或已有世界刷新)。调用方负责节流上 UI。</summary>
    public event Action<LanWorld>? WorldDiscovered;

    /// <summary>世界消失(超过存活期没再收到广播)。</summary>
    public event Action<string>? WorldLost;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
                return _listenTask is { IsCompleted: false };
        }
    }

    /// <summary>当前发现到的存活世界。</summary>
    public IReadOnlyList<LanWorld> GetAliveWorlds()
    {
        // 顺手清理过期项
        foreach (var (key, world) in _worlds)
        {
            if (!world.IsAlive && _worlds.TryRemove(key, out _))
                WorldLost?.Invoke(key);
        }

        return _worlds.Values.Where(w => w.IsAlive).ToList();
    }

    /// <summary>开始监听局域网广播。</summary>
    public void Start()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(LanScanner));

        lock (_gate)
        {
            if (_listenTask is { IsCompleted: false })
                return;

            _cts = new CancellationTokenSource();

            try
            {
                var listener = new UdpClient();
                // 必须显式允许地址重用,否则游戏/其它启动器已占用 4445 时会绑定失败
                listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                listener.Client.Bind(new IPEndPoint(IPAddress.Any, MulticastPort));
                listener.JoinMulticastGroup(MulticastAddress);

                _listener = listener;
                _listenTask = Task.Run(() => ListenLoopAsync(listener, _cts.Token));
            }
            catch (SocketException)
            {
                // 端口被占用等情况:广播发现不可用,由调用方回退到端口扫描
                _listener?.Dispose();
                _listener = null;
                _listenTask = null;
                _cts.Dispose();
                _cts = null;
                throw;
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            try
            {
                _cts?.Cancel();
                _listener?.Dispose();
            }
            catch (Exception)
            {
                // 停止阶段的异常无需处理
            }

            _listener = null;
            _listenTask = null;
            _cts?.Dispose();
            _cts = null;
            _worlds.Clear();
        }
    }

    private async Task ListenLoopAsync(UdpClient listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await listener.ReceiveAsync(ct);
                var text = Encoding.UTF8.GetString(result.Buffer);

                var world = ParseBroadcast(text, result.RemoteEndPoint.Address.ToString());
                if (world is null)
                    continue;

                var isNew = !_worlds.ContainsKey(world.Address + ":" + world.Port);
                _worlds[world.Address + ":" + world.Port] = world;

                if (isNew)
                    WorldDiscovered?.Invoke(world);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // 单次接收失败(如网卡切换)不应终止整个监听
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 解析广播报文。格式固定为:
    /// <c>[MOTD]房间名[/MOTD][AD]端口[/AD]</c>
    /// </summary>
    public static LanWorld? ParseBroadcast(string text, string address)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        var motdMatch = Regex.Match(text, @"\[MOTD\](.*?)\[/MOTD\]", RegexOptions.Singleline);
        var adMatch = Regex.Match(text, @"\[AD\](\d+)\[/AD\]");

        if (!adMatch.Success)
            return null;

        if (!int.TryParse(adMatch.Groups[1].Value, out var port) || port is <= 0 or > 65535)
            return null;

        // MOTD 可能是多行的(房间名里带换行),拍平成一行便于展示。
        // 注意:名称可能是空的(正则仍会匹配成功),空名称在列表里会显示成一个空白条目,
        // 所以这里必须连同空串一起兜底成占位名。
        var motd = motdMatch.Success
            ? motdMatch.Groups[1].Value.Replace("\n", " ").Replace("\r", " ").Trim()
            : "";

        if (string.IsNullOrWhiteSpace(motd))
            motd = "(未命名世界)";

        return new LanWorld(motd, port, address, DateTime.Now);
    }

    // ===== 兜底:网段端口扫描 =====

    /// <summary>
    /// 扫描本机所在网段,找出开着指定端口的地址(默认 25565)。
    /// 用于组播被防火墙拦截的场景;结果不含房间名,只是"疑似可加入"。
    /// </summary>
    public static async Task<IReadOnlyList<LanWorld>> ScanSubnetAsync(
        int port = 25565,
        int timeoutMs = 300,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var results = new ConcurrentDictionary<string, LanWorld>();
        var addresses = GetLocalSubnetAddresses().ToList();

        if (addresses.Count == 0)
            return [];

        var scanned = 0;
        var total = addresses.Count;

        await Parallel.ForEachAsync(
            addresses,
            new ParallelOptions { MaxDegreeOfParallelism = 64, CancellationToken = ct },
            async (address, token) =>
            {
                if (await IsPortOpenAsync(address, port, timeoutMs, token))
                {
                    results[address] = new LanWorld(
                        $"疑似局域网世界({address})", port, address, DateTime.Now);
                }

                var done = Interlocked.Increment(ref scanned);
                progress?.Report(done * 100 / total);
            });

        return results.Values.ToList();
    }

    private static async Task<bool> IsPortOpenAsync(string host, int port, int timeoutMs, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            await client.ConnectAsync(host, port, cts.Token);
            return client.Connected;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 枚举本机各网卡的 /24 网段地址(只扫同一网段,不跨网段乱扫)。
    /// </summary>
    private static IEnumerable<string> GetLocalSubnetAddresses()
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            foreach (var info in adapter.GetIPProperties().UnicastAddresses)
            {
                if (info.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;

                var bytes = info.Address.GetAddressBytes();
                // 只扫常见的家庭/办公网段,避免在公网地址上做无意义的扫描
                if (bytes[0] != 192 && bytes[0] != 10 && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31))
                    continue;

                var prefix = $"{bytes[0]}.{bytes[1]}.{bytes[2]}.";
                for (var i = 1; i <= 254; i++)
                    yield return prefix + i;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }
}