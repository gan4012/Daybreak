using Daybreak.Core.Game;
using Xunit;
using Xunit.Abstractions;

namespace Daybreak.Core.Tests;

/// <summary>下载引擎真实网络集成测试:强制小分片,验证分片路径在真实服务器上可用。</summary>
public class DownloadEngineIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public DownloadEngineIntegrationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task DownloadFile_MultiChunk_OverRealNetwork()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "daybreak-engine-test", Guid.NewGuid().ToString("N"));
        // 8KB 分片 → 清单(~50KB)会被拆成多个分片
        var engine = new DownloadEngine("official", chunkSize: 8 * 1024, maxFileChunks: 8);
        var target = Path.Combine(tempDir, "version_manifest_v2.json");

        try
        {
            using var http = new HttpClient(new ResilientHttpHandler("official"));
            await engine.DownloadFileAsync(
                http, "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json", target,
                cancellationToken: CancellationToken.None);

            var json = File.ReadAllText(target);
            Assert.Contains("latest", json);
            Assert.True(new FileInfo(target).Length > 10_000, "清单过小,下载可能不完整");
            _output.WriteLine($"清单大小:{new FileInfo(target).Length} 字节(多分片下载成功)");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* 清理失败不影响断言 */ }
        }
    }
}
