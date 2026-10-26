using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Daybreak.Core.Server;

/// <summary>服务端类型。</summary>
public static class ServerType
{
    public const string Vanilla = "vanilla";
    public const string Forge = "forge";
    public const string Fabric = "fabric";
    public const string Paper = "paper";

    public static readonly string[] All = [Vanilla, Forge, Fabric, Paper];

    /// <summary>展示名(UI 用)。</summary>
    public static string DisplayName(string type) => type switch
    {
        Vanilla => "原版",
        Forge => "Forge",
        Fabric => "Fabric",
        Paper => "Paper",
        _ => type,
    };

    public static bool IsValid(string? type) => type is not null && All.Contains(type);
}

/// <summary>服务端运行状态。</summary>
public enum ServerState
{
    Stopped,
    Starting,
    Running,
    Stopping,
}

/// <summary>
/// 一个服务端实例的持久化元数据。
/// 目录布局:&lt;游戏目录&gt;/daybreak-servers/&lt;Name&gt;/,
/// 元数据存在该目录下的 instance.json(与 server.jar / world / logs 同级)。
/// </summary>
public class ServerInstance
{
    /// <summary>实例名,同时作为目录名(创建时已过滤非法字符)。</summary>
    public string Name { get; set; } = "";

    /// <summary>ServerType 常量之一。</summary>
    public string Type { get; set; } = ServerType.Vanilla;

    /// <summary>游戏版本,如 1.20.1。</summary>
    public string GameVersion { get; set; } = "";

    /// <summary>加载器版本:Fabric 为 loader 版本,Forge 为 forge 版本,Paper 为构建号,Vanilla 为空。</summary>
    public string? LoaderVersion { get; set; }

    /// <summary>端口(server.properties 的 server-port)。</summary>
    public int Port { get; set; } = 25565;

    /// <summary>最大堆内存(MB)。</summary>
    public int MaxMemoryMb { get; set; } = 2048;

    /// <summary>最小堆内存(MB)。</summary>
    public int MinMemoryMb { get; set; } = 1024;

    /// <summary>启动用 Java 路径;空则启动时自动匹配。</summary>
    public string? JavaPath { get; set; }

    /// <summary>额外 JVM 参数(Paper 会自动套用官方推荐参数,这里存用户覆盖值)。</summary>
    public string? JvmArguments { get; set; }

    /// <summary>用户是否已同意 Minecraft EULA(必须显式同意,不默认勾选)。</summary>
    public bool AcceptEula { get; set; }

    /// <summary>是否已确认过"离线模式风险"提示(online-mode=false 时)。</summary>
    public bool AcknowledgedOfflineRisk { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime? LastStartedAt { get; set; }

    /// <summary>服务端核心文件名(Fabric 是 launcher jar,Forge 是 run 脚本,其余是 server.jar)。</summary>
    public string? LaunchTarget { get; set; }

    // ---- 以下为运行时状态,不持久化 ----

    [JsonIgnore]
    public ServerState State { get; set; } = ServerState.Stopped;

    [JsonIgnore]
    public int? ProcessId { get; set; }

    [JsonIgnore]
    public string TypeDisplay => ServerType.DisplayName(Type);

    /// <summary>"1.20.1 · Fabric 0.15.11" 这类一行摘要。</summary>
    [JsonIgnore]
    public string VersionDisplay => string.IsNullOrEmpty(LoaderVersion)
        ? GameVersion
        : Type == ServerType.Paper
            ? $"{GameVersion} · Paper #{LoaderVersion}"
            : $"{GameVersion} · {TypeDisplay} {LoaderVersion}";

    public ServerInstance Clone() => (ServerInstance)MemberwiseClone();
}

/// <summary>服务端实例列表的持久化容器(存 &lt;游戏目录&gt;/daybreak-servers.json)。</summary>
public class ServerStore
{
    public List<ServerInstance> Servers { get; set; } = [];

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static ServerStore Load(string gameDirectory)
    {
        var path = StorePath(gameDirectory);
        if (!File.Exists(path))
            return new ServerStore();

        try
        {
            return JsonSerializer.Deserialize<ServerStore>(File.ReadAllText(path), Options)
                   ?? new ServerStore();
        }
        catch (JsonException)
        {
            // 列表损坏时回退空表,不影响已有实例目录
            return new ServerStore();
        }
    }

    public void Save(string gameDirectory)
    {
        Directory.CreateDirectory(gameDirectory);
        File.WriteAllText(StorePath(gameDirectory), JsonSerializer.Serialize(this, Options));
    }

    public static string StorePath(string gameDirectory) =>
        Path.Combine(gameDirectory, "daybreak-servers.json");
}