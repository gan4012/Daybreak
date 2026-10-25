using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using Daybreak.Core.Game;

namespace Daybreak.Core.Mod;

/// <summary>
/// CurseForge 整合包(zip,含 manifest.json)导入服务,无需 CurseForge API key:
/// 文件解析走 cfwidget 免费接口 → 拼 forgecdn CDN 直链 → 自研引擎下载。
/// </summary>
public class CurseForgeImportService
{
    private const string CfWidgetApi = "https://api.cfwidget.com/";
    private const string ForgeCdnBase = "https://mediafilez.forgecdn.net/files/";

    private readonly string _mirror;
    private readonly HttpClient _http;

    public CurseForgeImportService(string mirror = "official")
    {
        _mirror = mirror;
        _http = new HttpClient(new ResilientHttpHandler(mirror))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Daybreak-Launcher/0.1 (Minecraft launcher)");
    }

    /// <summary>判断 zip 是否为 CurseForge 整合包。</summary>
    public static bool IsCurseForgePack(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        return archive.GetEntry("manifest.json") is not null;
    }

    /// <summary>导入 CurseForge 整合包到目标版本(依赖文件进 mods,overrides 覆盖到游戏根目录)。</summary>
    public async Task ImportAsync(
        string versionId, string gameRoot, string zipPath,
        IProgress<LaunchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new InvalidOperationException("不是有效的 CurseForge 整合包(缺少 manifest.json)");

        using var manifestStream = manifestEntry.Open();
        using var manifest = await JsonDocument.ParseAsync(manifestStream, cancellationToken: cancellationToken);
        var root = manifest.RootElement;

        // 1. 解析依赖文件清单
        var requests = new List<(int ProjectId, int FileId)>();
        if (root.TryGetProperty("files", out var filesElement))
        {
            foreach (var file in filesElement.EnumerateArray())
            {
                var projectId = file.GetProperty("projectID").GetInt32();
                var fileId = file.GetProperty("fileID").GetInt32();
                var required = !file.TryGetProperty("required", out var req) || req.ValueKind == JsonValueKind.True;
                if (required)
                    requests.Add((projectId, fileId));
            }
        }

        // 2. 解析 overrides 清单数量(进度用)
        var overrideEntries = archive.Entries
            .Where(e => e.FullName.StartsWith("overrides/", StringComparison.Ordinal) && e.Length > 0)
            .ToList();

        var totalTasks = requests.Count + overrideEntries.Count;

        // 3. 逐项目解析下载地址并下载(每项目一次 cfwidget 查询,带缓存与队列重试)
        var doneFiles = 0;
        var projectCache = new Dictionary<int, Dictionary<int, string>>(); // projectId → (fileId → filename)
        Directory.CreateDirectory(gameRoot);
        var modsDir = Path.Combine(gameRoot, "mods");
        var engine = new DownloadEngine(_mirror);

        foreach (var (projectId, fileId) in requests)
        {
            if (!projectCache.TryGetValue(projectId, out var fileMap))
            {
                fileMap = await ResolveProjectFilesAsync(projectId, cancellationToken);
                projectCache[projectId] = fileMap;
            }

            if (!fileMap.TryGetValue(fileId, out var filename))
                throw new InvalidOperationException(
                    $"CurseForge 项目 {projectId} 中找不到文件 {fileId}(可能是 alpha 文件,已被移除)");

            var directUrl = ForgeCdnBase +
                $"{fileId / 1000}/{fileId % 1000}/{Uri.EscapeDataString(filename)}";
            var targetPath = Path.Combine(modsDir, filename);

            cancellationToken.ThrowIfCancellationRequested();
            await engine.DownloadFileAsync(
                _http, directUrl, targetPath,
                expectedSize: null, sha1: null,
                cancellationToken);

            doneFiles++;
            progress?.Report(new LaunchProgress
            {
                CurrentFile = filename,
                ProgressedTasks = doneFiles,
                TotalTasks = totalTasks,
            });
        }

        // 4. 解压 overrides 到游戏根目录
        foreach (var entry in overrideEntries)
        {
            var relative = entry.FullName["overrides/".Length..]
                .Replace('/', Path.DirectorySeparatorChar);
            var targetPath = Path.Combine(gameRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            entry.ExtractToFile(targetPath, overwrite: true);

            doneFiles++;
            progress?.Report(new LaunchProgress
            {
                CurrentFile = relative,
                ProgressedTasks = doneFiles,
                TotalTasks = totalTasks,
            });
        }
    }

    /// <summary>
    /// 查询一个 CurseForge 项目的全部文件(fileId → 文件名)。
    /// cfwidget 对未缓存项目返回 in-queue 响应,自动重试。
    /// </summary>
    private async Task<Dictionary<int, string>> ResolveProjectFilesAsync(
        int projectId, CancellationToken cancellationToken)
    {
        var url = CfWidgetApi + projectId;

        for (var attempt = 0; attempt < 6; attempt++)
        {
            var body = await _http.GetStringAsync(url, cancellationToken);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            // 队列中:{"in queue": true} → 等 3 秒重试
            if (root.TryGetProperty("in queue", out var queued) &&
                queued.ValueKind == JsonValueKind.True)
            {
                await Task.Delay(3000, cancellationToken);
                continue;
            }

            if (!root.TryGetProperty("files", out var files))
                throw new InvalidOperationException($"cfwidget 未返回项目 {projectId} 的文件列表");

            var map = new Dictionary<int, string>();
            foreach (var file in files.EnumerateArray())
            {
                var id = file.GetProperty("id").GetInt32();
                var name = file.GetProperty("name").GetString();
                if (name is not null)
                    map[id] = name;
            }
            return map;
        }

        throw new InvalidOperationException(
            $"cfwidget 解析项目 {projectId} 超时(服务器排队中),稍后重试");
    }
}
