using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using Daybreak.Core.Game;
using Daybreak.Core.Market;

namespace Daybreak.Core.Mod;

/// <summary>
/// Mod 安装服务:Modrinth 资源装入指定版本的 mods 目录,以及 mrpack 整合包导入。
/// 目录解析遵循版本隔离配置。
/// </summary>
public class ModInstallService(LaunchService launchService, string mirror = "official")
{
    /// <summary>安装一个 Modrinth 版本文件到目标版本的 mods 目录,返回落盘路径。</summary>
    public async Task<string> InstallModAsync(
        string versionId, ModrinthVersion modVersion,
        IProgress<LaunchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var file = modVersion.PrimaryFile
            ?? throw new InvalidOperationException($"{modVersion.VersionNumber} 没有可下载的文件");

        var modsDir = launchService.ResolveModsDirectory(versionId);
        Directory.CreateDirectory(modsDir);
        var targetPath = Path.Combine(modsDir, file.Filename);

        var engine = new DownloadEngine(mirror);
        await engine.DownloadFileAsync(
            new HttpClient(new ResilientHttpHandler(mirror)) { Timeout = System.Threading.Timeout.InfiniteTimeSpan }, file.Url, targetPath, file.Size, file.Sha1,
            cancellationToken);

        progress?.Report(new LaunchProgress
        {
            CurrentFile = file.Filename,
            ProgressedTasks = 1,
            TotalTasks = 1,
            ProgressedBytes = file.Size,
            TotalBytes = file.Size,
        });

        return targetPath;
    }

    /// <summary>
    /// 导入 mrpack 整合包(Modrinth 格式)到目标版本:
    /// 解析 modrinth.index.json → 并发下载全部文件 → 解压 overrides 覆盖到游戏根目录。
    /// </summary>
    public async Task ImportModrinthPackAsync(
        string versionId, string mrpackPath,
        IProgress<LaunchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var gameRoot = launchService.ResolveGameRootForVersion(versionId);
        Directory.CreateDirectory(gameRoot);

        using var archive = ZipFile.OpenRead(mrpackPath);
        var indexEntry = archive.GetEntry("modrinth.index.json")
            ?? throw new InvalidOperationException("不是有效的 Modrinth 整合包(缺少 modrinth.index.json)");

        using var indexStream = indexEntry.Open();
        using var document = await JsonDocument.ParseAsync(indexStream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        // 1. 下载依赖文件(mods 等)到 index.path 指定位置
        var downloads = new List<DownloadItem>();
        if (root.TryGetProperty("files", out var filesElement))
        {
            foreach (var file in filesElement.EnumerateArray())
            {
                // env 过滤:客户端不可用的跳过
                if (file.TryGetProperty("env", out var env) &&
                    env.TryGetProperty("client", out var client) &&
                    client.GetString() == "unsupported")
                    continue;

                var path = file.GetProperty("path").GetString()!
                    .Replace('/', Path.DirectorySeparatorChar);
                var targetPath = Path.Combine(gameRoot, path);
                var url = file.GetProperty("downloads").EnumerateArray().First().GetString()!;
                var size = file.TryGetProperty("fileSize", out var sizeElement) ? sizeElement.GetInt64() : 0;
                var sha1 = file.TryGetProperty("hashes", out var hashes) &&
                           hashes.TryGetProperty("sha1", out var sha1Element)
                    ? sha1Element.GetString()
                    : null;

                downloads.Add(new DownloadItem(url, targetPath, path, size, sha1));
            }
        }

        var totalBytes = downloads.Sum(d => d.Size);
        var engine = new DownloadEngine(mirror);
        await engine.DownloadFilesAsync(
            new HttpClient(new ResilientHttpHandler(mirror)) { Timeout = System.Threading.Timeout.InfiniteTimeSpan }, downloads,
            progress, cancellationToken);

        // 2. 解压 overrides 到游戏根目录
        var overridesEntry = archive.GetEntry("overrides/");
        if (overridesEntry is not null || archive.Entries.Any(e => e.FullName.StartsWith("overrides/", StringComparison.Ordinal)))
        {
            var totalEntries = archive.Entries.Count(e =>
                e.FullName.StartsWith("overrides/", StringComparison.Ordinal) && e.Length > 0);
            var done = 0;
            foreach (var entry in archive.Entries)
            {
                if (!entry.FullName.StartsWith("overrides/", StringComparison.Ordinal) || entry.Length == 0)
                    continue;

                var relative = entry.FullName["overrides/".Length..]
                    .Replace('/', Path.DirectorySeparatorChar);
                var targetPath = Path.Combine(gameRoot, relative);
                if (targetPath.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                    continue;

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                entry.ExtractToFile(targetPath, overwrite: true);

                done++;
                progress?.Report(new LaunchProgress
                {
                    CurrentFile = relative,
                    ProgressedTasks = done,
                    TotalTasks = totalEntries,
                    ProgressedBytes = totalBytes + (long)done * 1024,
                    TotalBytes = totalBytes + (long)totalEntries * 1024,
                });
            }
        }

        progress?.Report(new LaunchProgress
        {
            CurrentFile = "整合包导入完成",
            ProgressedTasks = 1,
            TotalTasks = 1,
        });
    }
}
