using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using Daybreak.Core.Game;
using Daybreak.Core.Market;

namespace Daybreak.Core.Mod;

/// <summary>单个 Mod 的安装记录(用于精确的更新检查)。</summary>
public record ModInstallRecord(string Filename, string ProjectId, string VersionNumber, string? Sha1);

/// <summary>
/// Mod 安装服务:Modrinth 资源装入指定版本的 mods 目录,以及 mrpack 整合包导入。
/// 目录解析遵循版本隔离配置。安装记录持久化到版本目录的 daybreak-mods.json。
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

        RecordInstall(versionId, new ModInstallRecord(
            file.Filename, modVersion.Id, modVersion.VersionNumber, file.Sha1));

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

    /// <summary>读取版本的 Mod 安装记录(文件不存在返回空表)。</summary>
    public IReadOnlyList<ModInstallRecord> GetInstallRecords(string versionId)
    {
        var path = RecordsPath(versionId);
        if (!File.Exists(path))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<ModInstallRecord>>(File.ReadAllText(path))
                   ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>更新某条安装记录(记录存在时)。</summary>
    public void RecordInstall(string versionId, ModInstallRecord record)
    {
        var records = GetInstallRecords(versionId).ToList();
        records.RemoveAll(r => r.Filename == record.Filename);
        records.Add(record);
        SaveRecords(versionId, records);
    }

    private string RecordsPath(string versionId)
    {
        // 记录跟随版本隔离:存放在该版本的游戏根目录(mods 目录同级)
        return Path.Combine(launchService.ResolveGameRootForVersion(versionId), "daybreak-mods.json");
    }

    private void SaveRecords(string versionId, List<ModInstallRecord> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RecordsPath(versionId))!);
        File.WriteAllText(RecordsPath(versionId),
            JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
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
