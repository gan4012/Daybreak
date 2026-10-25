using Daybreak.Core.Game;
using Daybreak.Core.Market;
using Daybreak.Core.Mod;
using Xunit;
using Xunit.Abstractions;

namespace Daybreak.Core.Tests;

/// <summary>M4 资源市场真实网络集成测试:搜 JEI → 装 JEI(fabric)到临时版本目录。</summary>
public class M4MarketIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public M4MarketIntegrationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task SearchJei_FindsResults()
    {
        var client = new ModrinthClient();
        var page = await client.SearchAsync("JEI", "mod", limit: 5);

        Assert.NotEmpty(page.Hits);
        Assert.Contains(page.Hits, h => h.Title.Contains("JEI", StringComparison.OrdinalIgnoreCase) ||
                                         h.Slug.Contains("jei", StringComparison.OrdinalIgnoreCase));
        _output.WriteLine($"共 {page.TotalHits} 个结果,首个:{page.Hits[0].Title} by {page.Hits[0].Author}");
    }

    [Fact]
    public async Task InstallJeiFabric_CompletesIntoIsolatedModsDir()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "daybreak-m4-test", Guid.NewGuid().ToString("N"));
        var launchService = new LaunchService(tempDir, "official", versionIsolation: true);
        var installService = new ModInstallService(launchService);
        var client = new ModrinthClient();

        try
        {
            var search = await client.SearchAsync("JEI", "mod", "1.20.1", "fabric", limit: 5);
            var jei = search.Hits.First(h => h.Slug.Contains("jei", StringComparison.OrdinalIgnoreCase));

            var versions = await client.GetVersionsAsync(jei.ProjectId, "1.20.1", "fabric");
            Assert.NotEmpty(versions);
            var version = versions[0];
            var file = version.PrimaryFile!;
            _output.WriteLine($"安装 {jei.Title} {version.VersionNumber}({file.Size / 1024} KB)");

            var installedPath = await installService.InstallModAsync(
                "1.20.1-fabric-test", version,
                new Progress<LaunchProgress>(p => _output.WriteLine($"  {p.Percent}% {p.CurrentFile}")));

            Assert.True(File.Exists(installedPath), "mod 文件未落盘");
            Assert.Contains($"{Path.DirectorySeparatorChar}mods{Path.DirectorySeparatorChar}", installedPath);
            Assert.True(installedPath.Contains("versions" + Path.DirectorySeparatorChar + "1.20.1-fabric-test", StringComparison.Ordinal),
                "隔离配置下 mod 应装进版本文件夹");
            Assert.Equal(file.Size, new FileInfo(installedPath).Length);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* 清理失败不影响断言 */ }
        }
    }
}
