using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;

namespace Daybreak.Core.Game;

/// <summary>批量下载项。</summary>
public record DownloadItem(string Url, string TargetPath, string Name, long Size = 0, string? Sha1 = null);

/// <summary>
/// 自研多线程下载引擎:
/// - 单文件按 Range 分片并发下载(服务器不支持 Range 时自动回退单流)
/// - 每分片独立重试,请求层经 ResilientHttpHandler 做 镜像→官方 降级
/// - 下载完成后 SHA1 校验,.part 原子改名
/// - 批量模式多文件并发,聚合字节进度
/// </summary>
public class DownloadEngine
{
    private readonly string _mirror;
    private readonly int _chunkSize;
    private readonly int _maxFileChunks;
    private readonly int _maxParallelFiles;

    public DownloadEngine(
        string mirror,
        int chunkSize = 4 * 1024 * 1024,
        int maxFileChunks = 8,
        int maxParallelFiles = 8)
    {
        _mirror = mirror;
        _chunkSize = chunkSize;
        _maxFileChunks = maxFileChunks;
        _maxParallelFiles = maxParallelFiles;
    }

    private HttpClient CreateHttp() => new(new ResilientHttpHandler(_mirror));

    /// <summary>线程安全的字节进度聚合器。</summary>
    private sealed class ByteCounter
    {
        private long _completed;

        public long Total { get; }

        public ByteCounter(long total) => Total = total;

        public void Add(long bytes) => Interlocked.Add(ref _completed, bytes);

        public long Completed => Interlocked.Read(ref _completed);
    }

    /// <summary>下载单个文件(分片并发)。</summary>
    public async Task DownloadFileAsync(
        HttpClient http, string url, string targetPath,
        long expectedSize = 0, string? sha1 = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

        if (expectedSize == 0)
        {
            // 清单里的空文件(如 jre 的某些占位文件),直接落一个空文件
            using (File.Create(targetPath)) { }
            return;
        }

        var partPath = targetPath + ".part";

        // 探测总长度与 Range 支持
        var (totalSize, rangeSupported) = await ProbeAsync(http, url, cancellationToken);

        if (expectedSize > 0 && totalSize > 0 && totalSize != expectedSize)
            throw new InvalidOperationException(
                $"文件大小不符:{Path.GetFileName(targetPath)} 预期 {expectedSize},实际 {totalSize}");

        // 清理与目标同名但内容已完整的 .part(断点续传场景:本次简单实现为整体重下)
        if (File.Exists(partPath))
            File.Delete(partPath);

        if (rangeSupported && totalSize > _chunkSize)
            await DownloadChunkedAsync(http, url, partPath, totalSize, cancellationToken);
        else
            await DownloadSingleStreamAsync(http, url, partPath, cancellationToken);

        VerifySize(partPath, totalSize > 0 ? totalSize : expectedSize, targetPath);
        VerifySha1(partPath, sha1, targetPath);
        File.Move(partPath, targetPath, overwrite: true);
    }

    private async Task<(long Total, bool RangeSupported)> ProbeAsync(
        HttpClient http, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            return (0, false); // 空资源

        response.EnsureSuccessStatusCode();

        if (response.StatusCode == HttpStatusCode.PartialContent &&
            response.Content.Headers.ContentRange?.Length is long length)
            return (length, true);

        // 不支持 Range:整体长度从完整响应拿(但这里只读了 0-0,退回无 Range 全量再取)
        // 简化:ContentLength 可能是 1;标记为不支持 Range,单流下载时自行得知长度
        return (0, false);
    }

    private static async Task DownloadSingleStreamAsync(
        HttpClient http, string url, string partPath, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        await source.CopyToAsync(target, 1 << 20, ct);
    }

    private async Task DownloadChunkedAsync(
        HttpClient http, string url, string partPath, long totalSize, CancellationToken ct)
    {
        // 预分配文件
        await using (var allocator = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.Write))
        {
            allocator.SetLength(totalSize);
        }

        var chunkCount = (int)Math.Ceiling((double)totalSize / _chunkSize);
        var exceptions = new List<Exception>();
        var lockObject = new object();

        await Parallel.ForEachAsync(
            Enumerable.Range(0, chunkCount),
            new ParallelOptions { MaxDegreeOfParallelism = _maxFileChunks, CancellationToken = ct },
            async (chunkIndex, token) =>
            {
                var start = (long)chunkIndex * _chunkSize;
                var end = Math.Min(start + _chunkSize, totalSize) - 1;

                try
                {
                    await DownloadChunkAsync(http, url, partPath, start, end, token);
                }
                catch (Exception ex)
                {
                    lock (lockObject)
                    {
                        exceptions.Add(ex);
                    }
                }
            });

        if (exceptions.Count > 0)
            throw new AggregateException($"分片下载失败({exceptions.Count}/{chunkCount})", exceptions);
    }

    private static async Task DownloadChunkAsync(
        HttpClient http, string url, string partPath, long start, long end, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var target = new FileStream(
                    partPath, FileMode.Open, FileAccess.Write, FileShare.Write, 1 << 16);
                target.Seek(start, SeekOrigin.Begin);
                await source.CopyToAsync(target, 1 << 16, ct);

                // 校验分片实际写入长度
                if (target.Position - start != end - start + 1)
                    throw new IOException($"分片 {start}-{end} 写入长度不符");

                return;
            }
            catch (Exception ex) when (
                attempt < 2 &&
                ex is HttpRequestException or IOException &&
                !ct.IsCancellationRequested)
            {
                await Task.Delay(500 * (attempt + 1), ct);
            }
        }
    }

    private static void VerifySize(string path, long expected, string displayName)
    {
        if (expected > 0)
        {
            var actual = new FileInfo(path).Length;
            if (actual != expected)
                throw new InvalidOperationException(
                    $"文件大小不符:{displayName} 预期 {expected},实际 {actual}");
        }
    }

    private static void VerifySha1(string path, string? sha1, string displayName)
    {
        if (string.IsNullOrEmpty(sha1))
            return;

        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexStringLower(SHA1.HashData(stream));
        if (actual != sha1)
            throw new InvalidOperationException($"SHA1 校验失败:{displayName}");
    }

    /// <summary>批量下载(多文件并发,聚合进度,TotalBytes 汇总)。</summary>
    public async Task DownloadFilesAsync(
        HttpClient http, IEnumerable<DownloadItem> items,
        IProgress<LaunchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var list = items.ToList();
        var counter = new ByteCounter(list.Sum(i => i.Size));

        var reportLock = new object();
        await Parallel.ForEachAsync(
            list,
            new ParallelOptions { MaxDegreeOfParallelism = _maxParallelFiles, CancellationToken = cancellationToken },
            async (item, token) =>
            {
                await DownloadFileAsync(http, item.Url, item.TargetPath,
                    item.Size, item.Sha1, token);
                counter.Add(item.Size);

                if (progress is not null)
                {
                    lock (reportLock)
                    {
                        progress.Report(new LaunchProgress
                        {
                            CurrentFile = item.Name,
                            ProgressedBytes = counter.Completed,
                            TotalBytes = counter.Total,
                            ProgressedTasks = list.IndexOf(item) + 1,
                            TotalTasks = list.Count,
                        });
                    }
                }
            });
    }
}
