using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Daybreak.Core.Game;
using Daybreak.Core.Mod;
using Xunit;
using Xunit.Abstractions;

namespace Daybreak.Core.Tests;

/// <summary>
/// CurseForge 整合包导入的真实数据集成测试。
///
/// 此前该路径"逻辑同 mrpack,理论可用"但从未实测。这里用**真实的 CurseForge 项目 ID**
/// (JEI 238222)从 cfwidget 查到真实 fileID,构造符合 CF 规范的 manifest.json,
/// 从而真实走过:manifest 解析 → cfwidget 查文件名 → forgecdn 直链下载 → overrides 解压。
/// </summary>
[Trait("Category", "Integration")]
public class M4CurseForgeIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public M4CurseForgeIntegrationTests(ITestOutputHelper output) => _output = output;

    /// <summary>CurseForge 上 JEI 的项目 ID(长期稳定的知名 Mod)。</summary>
    private const int JeiProjectId = 238222;

    [Fact]
    public async Task ImportCurseForgePack_DownloadsRealModAndExtractsOverrides()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Daybreak-Test/1.0");

        // ---- 1. 从 cfwidget 取真实 fileID(未缓存项目会返回排队中,需重试)----
        var (fileId, fileName) = await ResolveRealCfFileAsync(http, JeiProjectId);
        _output.WriteLine($"cfwidget 解析:project={JeiProjectId} file={fileId} name={fileName}");

        var tempDir = Path.Combine(Path.GetTempPath(), "daybreak-cf-test", Guid.NewGuid().ToString("N"));
        var packPath = Path.Combine(tempDir, "test-pack.zip");
        var gameRoot = Path.Combine(tempDir, "game");
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(gameRoot);

        try
        {
            // ---- 2. 构造真实的 CurseForge 整合包结构 ----
            BuildCurseForgePack(packPath, (JeiProjectId, fileId));

            Assert.True(CurseForgeImportService.IsCurseForgePack(packPath),
                "构造的 zip 未被识别为 CurseForge 整合包");

            // ---- 3. 执行导入 ----
            var service = new CurseForgeImportService(DownloadMirror.Official);
            var progress = new Progress<LaunchProgress>(p =>
            {
                if (!string.IsNullOrEmpty(p.CurrentFile))
                    _output.WriteLine($"  [{p.ProgressedTasks}/{p.TotalTasks}] {p.CurrentFile}");
            });

            _output.WriteLine("开始导入…");
            await service.ImportAsync("1.20.1-cf-test", gameRoot, packPath, progress);

            // ---- 4. 断言:Mod 落到 mods/ ----
            var modsDir = Path.Combine(gameRoot, "mods");
            Assert.True(Directory.Exists(modsDir), "mods 目录未创建");

            var modFiles = Directory.GetFiles(modsDir);
            Assert.NotEmpty(modFiles);

            var modPath = modFiles[0];
            var modInfo = new FileInfo(modPath);
            _output.WriteLine($"下载到:{modInfo.Name}({modInfo.Length / 1024} KB)");

            Assert.True(modInfo.Length > 10_000, "Mod 文件过小,下载可能不完整");
            // 应为有效 jar(zip magic: PK)
            var head = new byte[2];
            await using (var fs = File.OpenRead(modPath))
            {
                Assert.Equal(2, await fs.ReadAsync(head));
            }
            Assert.Equal((byte)'P', head[0]);
            Assert.Equal((byte)'K', head[1]);

            // ---- 5. 断言:overrides 解压到游戏根目录 ----
            var overrideFile = Path.Combine(gameRoot, "config", "daybreak-cf-override.txt");
            Assert.True(File.Exists(overrideFile), "overrides 未解压到游戏根目录");
            var overrideText = await File.ReadAllTextAsync(overrideFile);
            Assert.Contains("daybreak-override-ok", overrideText);

            _output.WriteLine("✅ CurseForge 整合包导入成功(mods + overrides 均正确)");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* 清理失败不影响断言 */ }
        }
    }

    /// <summary>构造符合 CurseForge 规范的整合包 zip:manifest.json + overrides/。</summary>
    private static void BuildCurseForgePack(string path, params (int ProjectId, int FileId)[] files)
    {
        var manifest = new
        {
            minecraft = new
            {
                version = "1.20.1",
                modLoaders = new[] { new { id = "forge-47.2.0", primary = true } },
            },
            manifestType = "minecraftModpack",
            manifestVersion = 1,
            name = "Daybreak CF Import Test",
            version = "1.0.0",
            author = "Daybreak",
            files = files.Select(f => new
            {
                projectID = f.ProjectId,
                fileID = f.FileId,
                required = true,
            }).ToArray(),
            overrides = "overrides",
        };

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        var manifestEntry = zip.CreateEntry("manifest.json");
        using (var stream = manifestEntry.Open())
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        }

        var overrideEntry = zip.CreateEntry("overrides/config/daybreak-cf-override.txt");
        using (var stream = overrideEntry.Open())
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write("daybreak-override-ok");
        }
    }

    /// <summary>查询 cfwidget 取真实 fileID;排队时重试。</summary>
    private static async Task<(int FileId, string FileName)> ResolveRealCfFileAsync(HttpClient http, int projectId)
    {
        var url = $"https://api.cfwidget.com/{projectId}";

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var body = await http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("in queue", out var queued) && queued.ValueKind == JsonValueKind.True)
            {
                await Task.Delay(3000);
                continue;
            }

            if (root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
            {
                var candidates = new List<(int Id, string Name)>();
                foreach (var f in files.EnumerateArray())
                {
                    if (!f.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
                        continue;
                    var name = f.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (!string.IsNullOrEmpty(name) && name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                        candidates.Add((idEl.GetInt32(), name!));
                }

                if (candidates.Count > 0)
                {
                    // 取 fileID 最大的(最新版),forgecdn 的 files/{id/1000}/{id%1000}/ 路径对新文件最可靠
                    var best = candidates.OrderByDescending(c => c.Id).First();
                    return (best.Id, best.Name);
                }
            }

            await Task.Delay(2000);
        }

        throw new InvalidOperationException($"cfwidget 未能解析项目 {projectId} 的文件列表");
    }
}