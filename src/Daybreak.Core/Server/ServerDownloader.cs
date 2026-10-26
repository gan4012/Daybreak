using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Daybreak.Core.Game;

namespace Daybreak.Core.Server;

/// <summary>可选的服务端版本条目。</summary>
public record ServerVersionOption(string GameVersion, string? LoaderVersion, string Display);

/// <summary>
/// 服务端核心下载器:Vanilla / Fabric / Paper。
///
/// 三者的共同点:**下载一个 jar 就能开服**,不需要跑外部安装器
/// (Forge 是唯一的例外,见 ForgeServerInstaller)。
///
/// 各源的实测结论(2026-10-26 验证):
/// - Vanilla:piston-data 的 downloads.server,带 SHA1;BMCLAPI 有镜像
/// - Fabric:必须用 4 段路径 .../loader/{游戏}/{加载器}/{安装器}/server/jar,3 段会 404
/// - Paper:旧 API api.papermc.io/v2 已下线(410),必须用 fill.papermc.io/v3,带 SHA256
/// </summary>
public class ServerDownloader
{
    private const string FabricMeta = "https://meta.fabricmc.net/v2/";
    private const string PaperApi = "https://fill.papermc.io/v3/projects/paper";
    private const string MojangManifest = "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";

    private readonly string _mirror;
    private readonly DownloadEngine _engine;

    public ServerDownloader(string mirror)
    {
        _mirror = DownloadMirror.IsValid(mirror) ? mirror : DownloadMirror.Official;
        _engine = new DownloadEngine(_mirror);
    }

    /// <summary>下载用 HttpClient:走镜像/重试处理器,长下载不设总超时。</summary>
    public static HttpClient CreateHttp(string mirror = DownloadMirror.Official) =>
        new(new ResilientHttpHandler(mirror)) { Timeout = Timeout.InfiniteTimeSpan };

    // ===== 版本列表 =====

    /// <summary>列出某服务端类型可用的游戏版本(新→旧)。</summary>
    public async Task<IReadOnlyList<ServerVersionOption>> GetVersionsAsync(
        string serverType, CancellationToken ct = default)
    {
        return serverType switch
        {
            ServerType.Vanilla => await GetVanillaVersionsAsync(ct),
            ServerType.Fabric => await GetFabricVersionsAsync(ct),
            ServerType.Paper => await GetPaperVersionsAsync(ct),
            // Forge 的版本列表与客户端共用 LoaderService,交给调用方
            _ => [],
        };
    }

    private async Task<IReadOnlyList<ServerVersionOption>> GetVanillaVersionsAsync(CancellationToken ct)
    {
        using var http = CreateHttp(_mirror);
        var json = await http.GetStringAsync(MojangManifest, ct);
        using var doc = JsonDocument.Parse(json);

        var list = new List<ServerVersionOption>();
        foreach (var v in doc.RootElement.GetProperty("versions").EnumerateArray())
        {
            // 只列正式版:快照/旧版的服务端支持情况复杂,先不暴露
            if (v.GetProperty("type").GetString() != "release")
                continue;

            var id = v.GetProperty("id").GetString();
            if (!string.IsNullOrEmpty(id))
                list.Add(new ServerVersionOption(id, null, id));
        }

        return list;
    }

    private async Task<IReadOnlyList<ServerVersionOption>> GetFabricVersionsAsync(CancellationToken ct)
    {
        using var http = CreateHttp(_mirror);

        // Fabric 的可用游戏版本来自 installer 的 game 列表
        var json = await http.GetStringAsync(FabricMeta + "versions/game", ct);
        using var doc = JsonDocument.Parse(json);

        var list = new List<ServerVersionOption>();
        foreach (var v in doc.RootElement.EnumerateArray())
        {
            if (!v.GetProperty("stable").GetBoolean())
                continue;

            var id = v.GetProperty("version").GetString();
            if (!string.IsNullOrEmpty(id))
                list.Add(new ServerVersionOption(id, null, id));
        }

        return list;
    }

    private async Task<IReadOnlyList<ServerVersionOption>> GetPaperVersionsAsync(CancellationToken ct)
    {
        using var http = CreateHttp(_mirror);
        var json = await http.GetStringAsync($"{PaperApi}", ct);
        using var doc = JsonDocument.Parse(json);

        var list = new List<ServerVersionOption>();
        if (!doc.RootElement.TryGetProperty("versions", out var versions))
            return list;

        foreach (var group in versions.EnumerateObject())
        {
            foreach (var v in group.Value.EnumerateArray())
            {
                var id = v.GetString();
                // 排除预发布/RC,只留正式版本号
                if (string.IsNullOrEmpty(id) ||
                    id.Contains("-pre", StringComparison.OrdinalIgnoreCase) ||
                    id.Contains("-rc", StringComparison.OrdinalIgnoreCase))
                    continue;

                list.Add(new ServerVersionOption(id, null, id));
            }
        }

        return list;
    }

    /// <summary>Fabric 的加载器版本列表。</summary>
    public async Task<IReadOnlyList<string>> GetFabricLoaderVersionsAsync(
        string gameVersion, CancellationToken ct = default)
    {
        using var http = CreateHttp(_mirror);
        var json = await http.GetStringAsync($"{FabricMeta}versions/loader/{gameVersion}", ct);
        using var doc = JsonDocument.Parse(json);

        var list = new List<string>();
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            var version = entry.GetProperty("loader").GetProperty("version").GetString();
            if (!string.IsNullOrEmpty(version))
                list.Add(version);
        }
        return list;
    }

    /// <summary>Paper 某游戏版本的可用构建号(新→旧,只含 STABLE)。</summary>
    public async Task<IReadOnlyList<string>> GetPaperBuildsAsync(
        string gameVersion, CancellationToken ct = default)
    {
        using var http = CreateHttp(_mirror);
        var json = await http.GetStringAsync($"{PaperApi}/versions/{gameVersion}/builds", ct);
        using var doc = JsonDocument.Parse(json);

        var list = new List<string>();
        foreach (var build in doc.RootElement.EnumerateArray())
        {
            if (build.TryGetProperty("channel", out var ch) &&
                ch.GetString() != "STABLE")
                continue;

            if (build.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number)
                list.Add(id.GetInt32().ToString());
        }
        return list;
    }

    /// <summary>Paper 官方为该版本推荐的 JVM 参数(实测来自 /versions/{ver} 接口)。</summary>
    public async Task<string?> GetPaperRecommendedJvmArgsAsync(
        string gameVersion, CancellationToken ct = default)
    {
        try
        {
            using var http = CreateHttp(_mirror);
            var json = await http.GetStringAsync($"{PaperApi}/versions/{gameVersion}", ct);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("version", out var version) ||
                !version.TryGetProperty("java", out var java) ||
                !java.TryGetProperty("flags", out var flags) ||
                !flags.TryGetProperty("recommended", out var recommended))
                return null;

            var args = recommended.EnumerateArray()
                .Select(a => a.GetString())
                .Where(a => !string.IsNullOrEmpty(a));

            return string.Join(' ', args);
        }
        catch (Exception)
        {
            // 推荐参数拿不到不影响开服,用默认参数即可
            return null;
        }
    }

    // ===== 安装 =====

    /// <summary>
    /// 下载服务端核心到目标目录,返回该实例应使用的启动目标(文件名)。
    /// Forge 不走这里。
    /// </summary>
    public async Task<string> InstallAsync(
        ServerInstance instance, string targetDirectory,
        IProgress<LaunchProgress>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(targetDirectory);

        return instance.Type switch
        {
            ServerType.Vanilla => await InstallVanillaAsync(instance, targetDirectory, progress, ct),
            ServerType.Fabric => await InstallFabricAsync(instance, targetDirectory, progress, ct),
            ServerType.Paper => await InstallPaperAsync(instance, targetDirectory, progress, ct),
            _ => throw new NotSupportedException($"不支持的服务端类型:{instance.Type}"),
        };
    }

    private async Task<string> InstallVanillaAsync(
        ServerInstance instance, string dir,
        IProgress<LaunchProgress>? progress, CancellationToken ct)
    {
        await DownloadVanillaServerJarAsync(instance.GameVersion, dir, progress, ct);
        return "server.jar";
    }

    /// <summary>
    /// 下载原版 server.jar。
    /// 原版自身要用它;Fabric 也需要它——fabric-server-launch.jar 依赖同目录的
    /// server.jar(见 fabric-server-launcher.properties 的 serverJar 项)来做运行时补丁。
    /// </summary>
    private async Task DownloadVanillaServerJarAsync(
        string gameVersion, string dir,
        IProgress<LaunchProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new LaunchProgress { CurrentFile = $"解析原版 {gameVersion} 的服务端下载地址…" });

        using var http = CreateHttp(_mirror);

        // 1. 从版本清单找到该版本的元数据
        var manifestJson = await http.GetStringAsync(MojangManifest, ct);
        using var manifest = JsonDocument.Parse(manifestJson);

        string? metaUrl = null;
        foreach (var v in manifest.RootElement.GetProperty("versions").EnumerateArray())
        {
            if (v.GetProperty("id").GetString() == gameVersion)
            {
                metaUrl = v.GetProperty("url").GetString();
                break;
            }
        }

        if (string.IsNullOrEmpty(metaUrl))
            throw new InvalidOperationException($"版本清单里找不到 {gameVersion}");

        // 2. 取 downloads.server
        var metaJson = await http.GetStringAsync(metaUrl, ct);
        using var meta = JsonDocument.Parse(metaJson);

        if (!meta.RootElement.TryGetProperty("downloads", out var downloads) ||
            !downloads.TryGetProperty("server", out var server))
        {
            throw new InvalidOperationException(
                $"{gameVersion} 没有官方服务端(过老的版本可能未提供 server.jar)");
        }

        var url = server.GetProperty("url").GetString()
                  ?? throw new InvalidOperationException("服务端下载地址为空");
        var sha1 = server.TryGetProperty("sha1", out var s) ? s.GetString() : null;
        var size = server.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;

        var target = Path.Combine(dir, "server.jar");
        progress?.Report(new LaunchProgress { CurrentFile = "下载 server.jar…", TotalBytes = size });

        await _engine.DownloadFileAsync(http, url, target, size, sha1, ct);
    }

    private async Task<string> InstallFabricAsync(
        ServerInstance instance, string dir,
        IProgress<LaunchProgress>? progress, CancellationToken ct)
    {
        using var http = CreateHttp(_mirror);

        // 1. 加载器版本:Fabric Meta 的第一个就是最新稳定版
        var loaderVersion = instance.LoaderVersion;
        if (string.IsNullOrEmpty(loaderVersion))
        {
            var loaders = await GetFabricLoaderVersionsAsync(instance.GameVersion, ct);
            loaderVersion = loaders.FirstOrDefault()
                ?? throw new InvalidOperationException($"Fabric 没有适配 {instance.GameVersion} 的加载器");
        }

        // 2. 安装器版本(注意:server/jar 端点是 4 段路径,缺安装器版本会 404)
        progress?.Report(new LaunchProgress { CurrentFile = "解析 Fabric 安装器版本…" });
        var installerJson = await http.GetStringAsync(FabricMeta + "versions/installer", ct);
        using var installerDoc = JsonDocument.Parse(installerJson);
        var installerVersion = installerDoc.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("version").GetString())
            .FirstOrDefault(v => !string.IsNullOrEmpty(v))
            ?? throw new InvalidOperationException("无法获取 Fabric 安装器版本");

        // 3. 下载服务端 launcher jar
        var url = $"{FabricMeta}versions/loader/{instance.GameVersion}/{loaderVersion}/{installerVersion}/server/jar";
        var target = Path.Combine(dir, "fabric-server-launch.jar");

        progress?.Report(new LaunchProgress { CurrentFile = "下载 Fabric 服务端…" });
        // 注意:DownloadEngine 的契约是 null=大小未知、0=已知空文件。
        // 这里必须传 null,Fabric 没有给出 Content-Length 之外的大小信息;
        // 传 0 会让它直接落一个 0 字节文件,启动时报 "Invalid or corrupt jarfile"。
        await _engine.DownloadFileAsync(http, url, target, null, null, ct);

        // 4. 记录加载器版本(供 UI 显示)
        instance.LoaderVersion = loaderVersion;

        // 5. Fabric 的服务端 launcher 需要同目录的原版 server.jar 才能完成运行时补丁,
        //    不预先下好会在首次启动时报 "server.jar not found"
        progress?.Report(new LaunchProgress { CurrentFile = "下载 Fabric 依赖的原版服务端…" });
        await DownloadVanillaServerJarAsync(instance.GameVersion, dir, progress, ct);

        // 6. 写 fabric-server-launcher.properties:明确告诉它用哪个原版 jar
        var propsPath = Path.Combine(dir, "fabric-server-launcher.properties");
        if (!File.Exists(propsPath))
        {
            File.WriteAllText(propsPath,
                "#Fabric 服务端启动配置" + Environment.NewLine +
                "serverJar=server.jar" + Environment.NewLine,
                new UTF8Encoding(false));
        }

        return "fabric-server-launch.jar";
    }

    private async Task<string> InstallPaperAsync(
        ServerInstance instance, string dir,
        IProgress<LaunchProgress>? progress, CancellationToken ct)
    {
        using var http = CreateHttp(_mirror);

        // 1. 构建号:不指定就取最新 STABLE
        var build = instance.LoaderVersion;
        if (string.IsNullOrEmpty(build))
        {
            var builds = await GetPaperBuildsAsync(instance.GameVersion, ct);
            build = builds.FirstOrDefault()
                ?? throw new InvalidOperationException($"Paper 没有 {instance.GameVersion} 的稳定构建");
        }

        progress?.Report(new LaunchProgress { CurrentFile = $"解析 Paper {instance.GameVersion} #{build}…" });

        // 2. 取构建详情里的下载信息(带 sha256 与大小)
        var json = await http.GetStringAsync($"{PaperApi}/versions/{instance.GameVersion}/builds", ct);
        using var doc = JsonDocument.Parse(json);

        JsonElement? chosen = null;
        foreach (var b in doc.RootElement.EnumerateArray())
        {
            if (b.TryGetProperty("id", out var id) &&
                id.ValueKind == JsonValueKind.Number &&
                id.GetInt32().ToString() == build)
            {
                chosen = b;
                break;
            }
        }

        if (chosen is null)
            throw new InvalidOperationException($"Paper 构建 #{build} 不存在");

        var downloads = chosen.Value.GetProperty("downloads");

        // server:default 是标准构建;server:mojang 是未打补丁的变体,不用
        if (!downloads.TryGetProperty("server:default", out var dl))
            throw new InvalidOperationException($"Paper 构建 #{build} 没有可下载的核心文件");

        var url = dl.GetProperty("url").GetString()
                  ?? throw new InvalidOperationException("Paper 下载地址为空");
        var fileName = dl.TryGetProperty("name", out var n) ? n.GetString() : null;
        var size = dl.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
        string? sha256 = null;
        if (dl.TryGetProperty("checksums", out var sums) &&
            sums.TryGetProperty("sha256", out var s256))
            sha256 = s256.GetString();

        // 3. 下载(DownloadEngine 只做 SHA1,这里下完再单独校验 SHA256)
        var target = Path.Combine(dir, "server.jar");
        progress?.Report(new LaunchProgress { CurrentFile = $"下载 {fileName ?? "paper.jar"}…", TotalBytes = size });

        await _engine.DownloadFileAsync(http, url, target, size, null, ct);

        if (!string.IsNullOrEmpty(sha256))
        {
            progress?.Report(new LaunchProgress { CurrentFile = "校验 SHA256…" });
            var actual = await Task.Run(() => ComputeSha256(target), ct);
            if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Paper 核心 SHA256 校验失败,文件可能损坏,请重试");
        }

        // 4. 记录构建号
        instance.LoaderVersion = build;

        // 5. Paper 的推荐 JVM 参数存进实例(用户可在 UI 覆盖)
        if (string.IsNullOrEmpty(instance.JvmArguments))
        {
            var recommended = await GetPaperRecommendedJvmArgsAsync(instance.GameVersion, ct);
            if (!string.IsNullOrEmpty(recommended))
                instance.JvmArguments = recommended;
        }

        return "server.jar";
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}