using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Daybreak.Core.Game;

/// <summary>
/// 微软官方 java-runtime 安装器:piston-meta 清单 → 组件 → 逐文件下载。
/// 支持 BMCLAPI 镜像改写;清单/文件均在可改写的 piston 域名上。
/// </summary>
public class MojangJavaInstaller
{
    private const string JavaManifestUrl =
        "https://launchermeta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";

    /// <summary>游戏要求的 Java 大版本 → 运行时组件。</summary>
    private static readonly IReadOnlyDictionary<int, string> ComponentByMajor = new Dictionary<int, string>
    {
        [8] = "jre-legacy",
        [17] = "java-runtime-gamma",
        [21] = "java-runtime-delta",
        [25] = "java-runtime-epsilon",
    };

    public const string DefaultComponent = "java-runtime-delta"; // Java 21,覆盖 1.18+ 主流版本

    public MojangJavaInstaller(string mirror) => _mirror = mirror;

    private readonly string _mirror;

    /// <summary>按游戏 Java 大版本选组件;未知一律 Java 21。</summary>
    public static string ResolveComponent(int? gameJavaMajor) => gameJavaMajor switch
    {
        null or <= 0 => DefaultComponent,
        <= 8 => ComponentByMajor[8],
        <= 17 => ComponentByMajor[17],
        <= 21 => ComponentByMajor[21],
        _ => ComponentByMajor[25],
    };

    /// <summary>
    /// 安装指定组件到 installRoot 下,返回运行时根目录(其 bin/java.exe 可直接使用)。
    /// 已存在时直接返回,不重复下载。
    /// </summary>
    public async Task<string> InstallAsync(
        string installRoot, string component, IProgress<LaunchProgress>? progress)
    {
        var runtimeDir = Path.Combine(installRoot, component);
        if (File.Exists(Path.Combine(runtimeDir, "bin", "java.exe")))
        {
            progress?.Report(new LaunchProgress { ProgressedTasks = 1, TotalTasks = 1, CurrentFile = "已安装" });
            return runtimeDir;
        }

        using var http = new HttpClient(new ResilientHttpHandler(_mirror));

        var manifestUrl = await ResolveManifestUrlAsync(http, component)
            ?? throw new InvalidOperationException($"清单中找不到组件 {component}(windows-x64)");

        var (files, totalBytes) = await LoadFileListAsync(http, manifestUrl);

        // 自研引擎批量下载:多文件并发 + 单文件分片 + SHA1 校验
        var engine = new DownloadEngine(_mirror);
        var items = files.Select(f => new DownloadItem(
            f.Url,
            Path.Combine(runtimeDir, f.Path.Replace('/', Path.DirectorySeparatorChar)),
            f.Path,
            f.Size,
            f.Sha1));

        await engine.DownloadFilesAsync(http, items, progress);

        return runtimeDir;
    }

    private static async Task<string?> ResolveManifestUrlAsync(HttpClient http, string component)
    {
        using var document = await ParseJsonAsync(http, JavaManifestUrl);
        var root = document.RootElement;

        if (!root.TryGetProperty("windows-x64", out var platform))
            return null;

        // 组件值可能是单个对象或对象数组,统一取第一个含 manifest.url 的条目
        JsonElement entry = default;
        if (platform.TryGetProperty(component, out var componentValue))
        {
            entry = componentValue.ValueKind == JsonValueKind.Array
                ? (componentValue.GetArrayLength() > 0 ? componentValue[0] : default)
                : componentValue;
        }

        if (entry.ValueKind == JsonValueKind.Object &&
            entry.TryGetProperty("manifest", out var manifest) &&
            manifest.TryGetProperty("url", out var url))
        {
            return url.GetString();
        }

        return null;
    }

    private static async Task<(List<(string Path, string Url, long Size, string? Sha1, bool Executable)> Files, long TotalBytes)>
        LoadFileListAsync(HttpClient http, string manifestUrl)
    {
        using var document = await ParseJsonAsync(http, manifestUrl);
        var files = document.RootElement.GetProperty("files");

        var result = new List<(string, string, long, string?, bool)>();
        long totalBytes = 0;

        foreach (var entry in files.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object ||
                !entry.Value.TryGetProperty("type", out var type) ||
                type.GetString() != "file")
                continue;

            var downloads = entry.Value.GetProperty("downloads");
            var raw = downloads.GetProperty("raw");
            var url = raw.GetProperty("url").GetString()
                ?? throw new InvalidOperationException($"文件 {entry.Name} 缺少下载地址");

            var size = raw.TryGetProperty("size", out var sizeElement) ? sizeElement.GetInt64() : 0;
            var sha1 = raw.TryGetProperty("sha1", out var sha1Element) ? sha1Element.GetString() : null;
            var executable = entry.Value.TryGetProperty("executable", out var execElement) && execElement.ValueKind == JsonValueKind.True;

            result.Add((entry.Name, url, size, sha1, executable));
            totalBytes += size;
        }

        // 小文件在前,进度条前期走得快,体验更好
        result.Sort((a, b) => a.Item3.CompareTo(b.Item3));
        return (result, totalBytes);
    }

    private static async Task<JsonDocument> ParseJsonAsync(HttpClient http, string url)
    {
        var bytes = await http.GetByteArrayAsync(url);
        // piston-meta 响应带 UTF-8 BOM,JsonDocument 不接受,手动剥掉
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            bytes = bytes[3..];
        return JsonDocument.Parse(bytes);
    }
}
