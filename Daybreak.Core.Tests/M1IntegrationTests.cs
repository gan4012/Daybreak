using Daybreak.Core.Game;
using Xunit;
using Xunit.Abstractions;

namespace Daybreak.Core.Tests;

/// <summary>
/// M1 联网集成测试:走真实网络与 CmlLib 管线。
/// 使用临时游戏目录,不污染用户 %APPDATA%。
/// </summary>
public class M1IntegrationTests
{
    private readonly ITestOutputHelper _output;

    public M1IntegrationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task GetOnlineVersions_ReturnsManifest()
    {
        var service = new LaunchService(null, DownloadMirror.Official);
        var versions = await service.GetOnlineVersionsAsync();

        Assert.NotEmpty(versions);
        Assert.Contains(versions, v => v.Type == "release");
        _output.WriteLine($"共 {versions.Count} 个版本,最新正式版:{versions.First(v => v.Type == "release").Name}");
    }

    [Fact]
    public async Task Install_SmallVersion_CompletesIntoTempDir()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "daybreak-m1-test", Guid.NewGuid().ToString("N"));
        var service = new LaunchService(tempDir, DownloadMirror.Official);
        var progress = new Progress<LaunchProgress>(p =>
            _output.WriteLine($"  {p.Percent}% {p.CurrentFile}"));

        try
        {
            await service.InstallAsync("rd-132211", progress);

            var installed = service.GetInstalledVersions();
            Assert.Contains("rd-132211", installed);

            var clientJar = Path.Combine(tempDir, "versions", "rd-132211", "rd-132211.jar");
            Assert.True(File.Exists(clientJar), "客户端 jar 未下载");
            Assert.True(new FileInfo(clientJar).Length > 10_000, "客户端 jar 过小,下载可能不完整");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* 清理失败不影响断言 */ }
        }
    }
}
