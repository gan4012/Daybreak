using Daybreak.Core.Game;
using Daybreak.Core.Mod;
using Xunit;
using Xunit.Abstractions;

namespace Daybreak.Core.Tests;

/// <summary>M3 加载器安装集成测试(真实网络,临时目录)。</summary>
public class M3LoaderIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public M3LoaderIntegrationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task InstallFabric_CompletesIntoTempDir()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "daybreak-m3-test", Guid.NewGuid().ToString("N"));
        var service = new LoaderService(tempDir, DownloadMirror.Official);
        var progress = new Progress<LaunchProgress>(p =>
            _output.WriteLine($"  {p.Percent}% {p.CurrentFile}"));

        try
        {
            var loaderVersions = await service.GetFabricLoaderVersionsAsync();
            Assert.NotEmpty(loaderVersions);
            _output.WriteLine($"Fabric loader 最新:{loaderVersions[0]}");

            var versionName = await service.InstallFabricAsync("1.20.1", loaderVersions[0], progress);
            _output.WriteLine($"安装完成:{versionName}");

            var versionsDir = Path.Combine(tempDir, "versions", versionName);
            Assert.True(File.Exists(Path.Combine(versionsDir, versionName + ".json")), "版本 JSON 缺失");
            Assert.True(File.Exists(Path.Combine(versionsDir, versionName + ".jar")), "客户端 jar 缺失");

            var json = File.ReadAllText(Path.Combine(versionsDir, versionName + ".json"));
            Assert.Contains(versionName, json);
            Assert.Contains("fabric", json);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* 清理失败不影响断言 */ }
        }
    }
}
