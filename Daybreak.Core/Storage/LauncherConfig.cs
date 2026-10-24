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

    /// <summary>离线模式玩家名</summary>
    public string PlayerName { get; set; } = "Player";

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
