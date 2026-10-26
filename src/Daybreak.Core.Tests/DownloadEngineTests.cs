using System.Net;
using System.Text;
using Daybreak.Core.Game;
using Xunit;

namespace Daybreak.Core.Tests;

/// <summary>自研下载引擎的离线桩测试:桩服务器支持 Range 语义。</summary>
public class DownloadEngineTests
{
    /// <summary>内存"服务器":支持普通 GET 与 Range 分段,可选不支持 Range。</span></summary>
    private sealed class RangeStubHandler(byte[] payload, bool supportRange = true) : HttpMessageHandler
    {
        public int RequestCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref RequestCount);

            Stream content;

            if (supportRange &&
                request.Headers.Range is not null &&
                request.Headers.Range.Ranges.Count > 0)
            {
                var range = request.Headers.Range.Ranges.First();
                var start = range.From ?? 0;
                var end = Math.Min(range.To ?? payload.Length - 1, payload.Length - 1);
                var length = end - start + 1;
                content = new MemoryStream(payload, (int)start, (int)length);
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new StreamContent(content),
                };
                response.Content.Headers.ContentLength = length;
                response.Content.Headers.ContentRange =
                    new System.Net.Http.Headers.ContentRangeHeaderValue(start, end, payload.Length);
                return Task.FromResult(response);
            }

            content = new MemoryStream(payload);
            var full = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(content),
            };
            full.Content.Headers.ContentLength = payload.Length;
            return Task.FromResult(full);
        }
    }

    private static byte[] MakePayload(int size)
    {
        var data = new byte[size];
        new Random(42).NextBytes(data);
        return data;
    }

    private static string Sha1Hex(byte[] data) => Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(data));

    [Fact]
    public async Task DownloadFile_Chunked_AssemblesExactBytes()
    {
        var payload = MakePayload(300_000); // 强制 3+ 分片(chunkSize=100KB)
        var handler = new RangeStubHandler(payload);
        var engine = new DownloadEngine("official", chunkSize: 100_000, maxFileChunks: 4);
        var target = Path.Combine(Path.GetTempPath(), "daybreak-dl-test", "chunked.bin");

        try
        {
            using var http = new HttpClient(handler);
            await engine.DownloadFileAsync(http, "https://stub/file.bin", target, payload.Length, Sha1Hex(payload));

            Assert.True(File.Exists(target));
            Assert.Equal(payload, await File.ReadAllBytesAsync(target));
            Assert.False(File.Exists(target + ".part"));
            // 4 个分片各 1 次请求
            Assert.Equal(4, handler.RequestCount);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(target)!, true);
        }
    }

    [Fact]
    public async Task DownloadFile_NoRangeSupport_FallsBackToSingleStream()
    {
        var payload = MakePayload(250_000);
        var handler = new RangeStubHandler(payload, supportRange: false);
        var engine = new DownloadEngine("official", chunkSize: 100_000);
        var target = Path.Combine(Path.GetTempPath(), "daybreak-dl-test", "norange.bin");

        try
        {
            using var http = new HttpClient(handler);
            await engine.DownloadFileAsync(http, "https://stub/file.bin", target, payload.Length, Sha1Hex(payload));
            Assert.Equal(payload, await File.ReadAllBytesAsync(target));
            Assert.Equal(2, handler.RequestCount); // 探测 1 次 + 全量 1 次
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(target)!, true);
        }
    }

    [Fact]
    public async Task DownloadFile_ShaMismatch_ThrowsAndLeavesNoTarget()
    {
        var payload = MakePayload(50_000);
        var handler = new RangeStubHandler(payload);
        var engine = new DownloadEngine("official", chunkSize: 100_000);
        var target = Path.Combine(Path.GetTempPath(), "daybreak-dl-test", "badsha.bin");

        try
        {
            using var http = new HttpClient(handler);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.DownloadFileAsync(http, "https://stub/file.bin", target, payload.Length, "deadbeef"));
            Assert.False(File.Exists(target), "校验失败不应留下最终文件");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(target)!, true);
        }
    }

    [Fact]
    public async Task DownloadFiles_ParallelBatch_CollectsAll()
    {
        var payloads = new[] { MakePayload(120_000), MakePayload(80_000), MakePayload(200_000) };
        var router = new RoutingHandler();
        var dir = Path.Combine(Path.GetTempPath(), "daybreak-dl-test", Guid.NewGuid().ToString("N"));
        var items = new List<DownloadItem>();
        for (var i = 0; i < payloads.Length; i++)
        {
            var url = $"https://stub/file{i}.bin";
            var target = Path.Combine(dir, $"file{i}.bin");
            router.Map(url, payloads[i]);
            items.Add(new DownloadItem(url, target, $"file{i}.bin", payloads[i].Length, Sha1Hex(payloads[i])));
        }

        try
        {
            var engine = new DownloadEngine("official", chunkSize: 100_000, maxParallelFiles: 3);
            var received = new List<LaunchProgress>();
            var progress = new Progress<LaunchProgress>(p => { lock (received) received.Add(p); });

            using var http = new HttpClient(router);
            await engine.DownloadFilesAsync(http, items, progress);

            for (var i = 0; i < payloads.Length; i++)
                Assert.Equal(payloads[i], await File.ReadAllBytesAsync(items[i].TargetPath));

            Assert.All(items, i => Assert.True(File.Exists(i.TargetPath)));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _routes = new();

        public void Map(string url, byte[] payload) => _routes[url] = payload;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = request.RequestUri!.ToString();
            var payload = _routes[key];

            if (request.Headers.Range?.Ranges.Count > 0)
            {
                var range = request.Headers.Range.Ranges.First();
                var start = range.From ?? 0;
                var end = Math.Min(range.To ?? payload.Length - 1, payload.Length - 1);
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new StreamContent(new MemoryStream(payload, (int)start, (int)(end - start + 1))),
                };
                response.Content.Headers.ContentLength = end - start + 1;
                return Task.FromResult(response);
            }

            var full = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(payload)),
            };
            full.Content.Headers.ContentLength = payload.Length;
            return Task.FromResult(full);
        }
    }
}
