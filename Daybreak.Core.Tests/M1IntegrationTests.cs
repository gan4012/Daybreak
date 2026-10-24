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

    [Fact]
    public async Task InstallJavaRuntime_CompletesIntoTempDir()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "daybreak-java-test", Guid.NewGuid().ToString("N"));
        var installer = new MojangJavaInstaller(DownloadMirror.Official);
        var progress = new Progress<LaunchProgress>(p =>
            _output.WriteLine($"  Java {p.Percent}% ({p.ProgressedTasks}/{p.TotalTasks}) {p.CurrentFile}"));

        try
        {
            // jre-legacy(Java 8)是官方清单里最小的运行时
            var runtimeDir = await installer.InstallAsync(tempDir, "jre-legacy", progress);

            var javaExe = Path.Combine(runtimeDir, "bin", "java.exe");
            Assert.True(File.Exists(javaExe), "bin/java.exe 不存在");

            var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = javaExe,
                Arguments = "-version",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            })!;
            var output = await proc.StandardError.ReadToEndAsync();
            proc.WaitForExit(30_000);
            _output.WriteLine($"java -version => {output.Trim()}");
            Assert.Contains("1.8", output);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* 清理失败不影响断言 */ }
        }
    }
}
