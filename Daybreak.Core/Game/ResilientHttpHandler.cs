using System.Net.Http;

namespace Daybreak.Core.Game;

/// <summary>
/// 韧性下载处理器:GET 请求失败时自动重试,并在镜像与官方源之间降级切换。
/// 尝试顺序:配置镜像 → 官方源 → 配置镜像(共 3 次)。
/// </summary>
public class ResilientHttpHandler : DelegatingHandler
{
    private readonly string _mirror;
    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.Zero,
        TimeSpan.FromMilliseconds(600),
        TimeSpan.FromSeconds(2),
    ];

    public ResilientHttpHandler(string mirror, HttpMessageHandler? innerHandler = null)
        : base(innerHandler ?? new HttpClientHandler())
    {
        _mirror = mirror;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var isGet = request.Method == HttpMethod.Get;

        if (!isGet)
            return await SendWithModeAsync(request, _mirror, cancellationToken);

        Exception? lastError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            // 降级顺序:配置镜像 → 官方 → 配置镜像
            var mode = attempt == 1 ? DownloadMirror.Official : _mirror;
            if (Backoff[attempt] > TimeSpan.Zero)
            {
                try { await Task.Delay(Backoff[attempt], cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            }

            try
            {
                return await SendWithModeAsync(request, mode, cancellationToken);
            }
            catch (Exception ex) when (
                ex is HttpRequestException or TaskCanceledException &&
                !cancellationToken.IsCancellationRequested)
            {
                lastError = ex;
            }
        }

        throw new HttpRequestException("下载失败(已尝试镜像与官方源各一次)", lastError);
    }

    private Task<HttpResponseMessage> SendWithModeAsync(
        HttpRequestMessage request, string mirror, CancellationToken cancellationToken)
    {
        var rewritten = DownloadMirror.Rewrite(request.RequestUri, mirror);
        if (!ReferenceEquals(rewritten, request.RequestUri))
        {
            var retry = new HttpRequestMessage(request.Method, rewritten)
            {
                Content = request.Content,
                Version = request.Version,
            };
            foreach (var header in request.Headers)
                retry.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return base.SendAsync(retry, cancellationToken);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
