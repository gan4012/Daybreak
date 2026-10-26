using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Daybreak.Core.Server;

/// <summary>
/// 收藏的服务器条目(用户手动添加的联机目标)。
/// 与 ServerInstance 的区别:ServerInstance 是「我开的服」,SavedServer 是「我要连的服」。
/// </summary>
public class SavedServer
{
    /// <summary>展示用名称(可空,空则显示 host)。</summary>
    public string Name { get; set; } = "";

    public string Host { get; set; } = "";

    public int Port { get; set; } = 25565;

    /// <summary>用户备注(比如"朋友的服,晚上开")。</summary>
    public string? Note { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.Now;

    /// <summary>是否置顶收藏。</summary>
    public bool Favorite { get; set; }

    // ---- 运行时状态(不持久化) ----

    [JsonIgnore]
    public ServerStatus? Status { get; set; }

    /// <summary>显示名:用户命名 > 主机名。</summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Host : Name;

    /// <summary>"host:port";默认端口时省略端口。</summary>
    [JsonIgnore]
    public string Address => Port == 25565 ? Host : $"{Host}:{Port}";

    [JsonIgnore]
    public bool IsOnline => Status?.Online == true;

    /// <summary>列表里显示的人数。</summary>
    [JsonIgnore]
    public string PlayerDisplay => Status?.PlayerDisplay ?? "—";

    /// <summary>延迟显示。</summary>
    [JsonIgnore]
    public string LatencyDisplay => Status is { Online: true } ? $"{Status.LatencyMs} ms" : "—";

    public SavedServer Clone() => (SavedServer)MemberwiseClone();
}

/// <summary>收藏服务器列表的持久化容器。</summary>
public class ServerListStore
{
    public List<SavedServer> Servers { get; set; } = [];

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string StorePath(string gameDirectory) =>
        Path.Combine(gameDirectory, "daybreak-serverlist.json");

    public static ServerListStore Load(string gameDirectory)
    {
        var path = StorePath(gameDirectory);
        if (!File.Exists(path))
            return new ServerListStore();

        try
        {
            return JsonSerializer.Deserialize<ServerListStore>(File.ReadAllText(path), Options)
                   ?? new ServerListStore();
        }
        catch (JsonException)
        {
            // 文件损坏时回退空表,不让启动器崩
            return new ServerListStore();
        }
    }

    public void Save(string gameDirectory)
    {
        Directory.CreateDirectory(gameDirectory);
        File.WriteAllText(StorePath(gameDirectory), JsonSerializer.Serialize(this, Options));
    }
}

/// <summary>
/// 服务器列表业务逻辑:增删改、批量刷新状态、一键直连参数组装。
/// </summary>
public class ServerListService
{
    private readonly string _gameDirectory;

    public ServerListService(string gameDirectory) => _gameDirectory = gameDirectory;

    public List<SavedServer> List() => ServerListStore.Load(_gameDirectory).Servers;

    /// <summary>
    /// 添加一个服务器。地址会被规范化(去掉 http:// 前缀、拆出端口)。
    /// 返回添加后的条目。
    /// </summary>
    public SavedServer Add(string addressInput, string? name = null, string? note = null)
    {
        var (host, port) = ParseAddress(addressInput);
        if (string.IsNullOrWhiteSpace(host))
            throw new InvalidOperationException("服务器地址不能为空");

        var store = ServerListStore.Load(_gameDirectory);

        if (store.Servers.Any(s => s.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && s.Port == port))
            throw new InvalidOperationException($"服务器 {host}:{port} 已在列表中");

        var server = new SavedServer
        {
            Name = name?.Trim() ?? "",
            Host = host,
            Port = port,
            Note = note,
        };

        store.Servers.Add(server);
        store.Save(_gameDirectory);
        return server;
    }

    public void Remove(string host, int port)
    {
        var store = ServerListStore.Load(_gameDirectory);
        store.Servers.RemoveAll(s =>
            s.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && s.Port == port);
        store.Save(_gameDirectory);
    }

    public void Update(SavedServer server)
    {
        var store = ServerListStore.Load(_gameDirectory);
        var index = store.Servers.FindIndex(s =>
            s.Host.Equals(server.Host, StringComparison.OrdinalIgnoreCase) && s.Port == server.Port);

        if (index >= 0)
            store.Servers[index] = server;
        else
            store.Servers.Add(server);

        store.Save(_gameDirectory);
    }

    /// <summary>
    /// 解析用户输入的服务器地址。支持这些写法:
    /// <c>mc.example.com</c> / <c>mc.example.com:25566</c> / <c>http://mc.example.com</c> /
    /// <c>1.2.3.4:25565</c>
    /// </summary>
    public static (string Host, int Port) ParseAddress(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return ("", 25565);

        var text = input.Trim();

        // 去掉 MCC 里常见的复制格式与协议前缀
        foreach (var prefix in new[] { "https://", "http://", "minecraft://" })
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[prefix.Length..];
                break;
            }
        }

        // 去掉路径部分
        var slash = text.IndexOf('/');
        if (slash >= 0)
            text = text[..slash];

        // 拆端口:只在最后一段是纯数字时当成端口(IPv6 里有冒号,不能简单 Split)
        var colon = text.LastIndexOf(':');
        if (colon > 0 && colon < text.Length - 1)
        {
            var portPart = text[(colon + 1)..];
            if (int.TryParse(portPart, out var port) && port is > 0 and <= 65535)
                return (text[..colon].Trim(), port);
        }

        return (text.Trim(), 25565);
    }

    /// <summary>
    /// 批量刷新在线状态。并发查询,单个失败不影响其它。
    /// onResult 会在每个结果回来时回调(UI 可据此逐条更新,不用等全部完成)。
    /// </summary>
    public async Task RefreshAllAsync(
        IReadOnlyList<SavedServer> servers,
        Action<SavedServer>? onResult = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        await Parallel.ForEachAsync(
            servers,
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (server, token) =>
            {
                var status = await ServerListPing.QueryAsync(server.Host, server.Port, timeout, token);
                server.Status = status;
                onResult?.Invoke(server);
            });
    }

    /// <summary>
    /// 组装"启动游戏并直连该服务器"的额外参数。
    /// Minecraft 客户端支持 --server / --port 直接进服。
    /// </summary>
    public static List<string> BuildDirectConnectArguments(SavedServer server)
    {
        // 注意:这里传原始域名而不是 SRV 目标,由游戏自己做 SRV 解析
        return ["--server", server.Host, "--port", server.Port.ToString()];
    }
}