using Daybreak.Core.Auth;
using Xunit;
using Xunit.Abstractions;

namespace Daybreak.Core.Tests;

/// <summary>authlib-injector 运行库下载集成测试(真实网络,体量小)。</summary>
[Trait("Category", "Integration")]
public class AuthlibInjectorTests
{
    private readonly ITestOutputHelper _output;

    public AuthlibInjectorTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task EnsureJar_DownloadsAndVerifies()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "daybreak-authlib-test", Guid.NewGuid().ToString("N"));
        var downloader = new AuthlibInjectorDownloader();

        try
        {
            var jarPath = await downloader.EnsureJarAsync(tempDir);
            Assert.True(File.Exists(jarPath), "jar 未下载");
            Assert.True(new FileInfo(jarPath).Length > 100_000, "jar 过小");
            _output.WriteLine($"jar: {jarPath} ({new FileInfo(jarPath).Length / 1024} KB)");

            // 二次调用应直接复用,不重复下载
            var again = await downloader.EnsureJarAsync(tempDir);
            Assert.Equal(jarPath, again);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* 清理失败不影响断言 */ }
        }
    }
}
