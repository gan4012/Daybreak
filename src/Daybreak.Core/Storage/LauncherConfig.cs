using System.IO;
using System.Text.Json;

namespace Daybreak.Core.Storage;

/// <summary>
/// 启动器全局配置。存于 %APPDATA%/Daybreak/config.json。
/// </summary>
public class LauncherConfig
{
    /// <summary>界面语言</summary>
    public string Language { get; set; } = "zh-CN";

    /// <summary>主题模式:Dark / Light / System</summary>
    public string ThemeMode { get; set; } = "Dark";

    /// <summary>背景效果:Acrylic / Mica / None</summary>
    public string Backdrop { get; set; } = "Acrylic";

    /// <summary>游戏根目录,null 表示默认 %APPDATA%/.minecraft</summary>
    public string? GameDirectory { get; set; }

    /// <summary>Java 主目录,null 表示自动探测</summary>
    public string? JavaDirectory { get; set; }

    /// <summary>游戏最大内存(MB)</summary>
    public int MaxMemoryMb { get; set; } = 4096;

    /// <summary>下载镜像:official / bmclapi</summary>
    public string Mirror { get; set; } = "official";

    /// <summary>版本隔离:各版本独立 saves/config 目录(共享 libraries/assets)。</summary>
    public bool VersionIsolation { get; set; } = true;

    /// <summary>离线模式玩家名</summary>
    public string PlayerName { get; set; } = "Player";

    /// <summary>
    /// 微软登录 Azure 应用 ClientId。null/空时使用 CmlLib 示例 id(仅开发),
    /// 发布前必须替换为自有应用的 ClientId。
    /// </summary>
    public string? MicrosoftClientId { get; set; }

    /// <summary>更新源 URL(最新版本元数据的原始地址);null 时使用默认 GitHub Releases。</summary>
    public string? UpdateSourceUrl { get; set; }

    /// <summary>自定义 JVM 参数(空格分隔,如 -XX:+UseG1GC)。不含 -Xmx(与内存设置冲突)。</summary>
    public string? JvmArguments { get; set; }

    /// <summary>代理主机地址,null/空表示不使用代理。</summary>
    public string? ProxyHost { get; set; }

    /// <summary>代理端口,0 表示未设置。</summary>
    public int ProxyPort { get; set; }

    /// <summary>代理类型:HTTP / SOCKS5。</summary>
    public string ProxyType { get; set; } = "HTTP";

    /// <summary>窗口左边缘位置(相对于屏幕)。</summary>
    public double? WindowLeft { get; set; }

    /// <summary>窗口上边缘位置(相对于屏幕)。</summary>
    public double? WindowTop { get; set; }

    /// <summary>窗口宽度。</summary>
    public double? WindowWidth { get; set; }

    /// <summary>窗口高度。</summary>
    public double? WindowHeight { get; set; }

    /// <summary>窗口是否最大化。</summary>
    public bool WindowMaximized { get; set; }

    /// <summary>
    /// 是否已完成首次启动引导。老用户升级上来时该字段为 false,
    /// 但配置文件已存在,据此可区分"真首次"与"老用户"(见 App 里的判断)。
    /// </summary>
    public bool HasCompletedFirstRun { get; set; }

    /// <summary>测试用存储目录重定向;生产环境必须保持为 null。</summary>
    internal static string? DirectoryOverrideForTests;

    [System.Text.Json.Serialization.JsonIgnore]
    public static string DefaultDirectory => DirectoryOverrideForTests
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Daybreak");

    public static string GetFilePath() => Path.Combine(DefaultDirectory, "config.json");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    public static LauncherConfig Load()
    {
        var path = GetFilePath();
        if (!File.Exists(path))
            return new LauncherConfig();

        try
        {
            return JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(path), SerializerOptions)
                   ?? new LauncherConfig();
        }
        catch (System.Text.Json.JsonException)
        {
            // 配置损坏时回退默认值,由下次 Save 覆盖
            return new LauncherConfig();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(DefaultDirectory);
        File.WriteAllText(GetFilePath(), JsonSerializer.Serialize(this, SerializerOptions));
    }
}
