using System.IO;
using System.Net;
using System.Net.Http;

namespace Daybreak.Core.Game;

/// <summary>
/// 韧性下载处理器:GET 请求失败时自动重试,并在镜像与官方源之间降级切换。
/// 尝试顺序:配置镜像 → 官方源 → 配置镜像(共 3 次)。
///
/// 每次尝试有独立限时:连接/TLS 15 秒、响应头 45 秒——拿到响应头后解除限时,
/// 正文按调用方节奏读取(大文件慢速下载不受影响)。
/// 这防止首选镜像黑洞(如 BMCLAPI 不可达)时请求永久挂起、重试永不触发。
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

    /// <summary>镜像健康记忆:最近一次成功的镜像,进程内全局共享,避免反复撞死镜像。</summary>
    private static volatile string? _lastGoodMirror;

    public ResilientHttpHandler(string mirror, HttpMessageHandler? innerHandler = null)
        : base(innerHandler ?? CreateInnerHandler())
    {
        _mirror = mirror;
    }

    private static HttpMessageHandler CreateInnerHandler() => new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(15),
        UseCookies = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    };

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var isGet = request.Method == HttpMethod.Get;

        if (!isGet)
            return await SendWithModeAsync(request, _mirror, cancellationToken);

        Exception? lastError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            // 降级顺序:上次成功的镜像 → 配置镜像 → 官方(健康记忆避免反复撞死镜像)
            var mode = attempt switch
            {
                0 => _lastGoodMirror ?? _mirror,
                1 => _mirror == DownloadMirror.Official ? DownloadMirror.Bmclapi : DownloadMirror.Official,
                _ => _mirror,
            };
            if (Backoff[attempt] > TimeSpan.Zero)
            {
                try { await Task.Delay(Backoff[attempt], cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            }

            try
            {
                var response = await SendWithModeAsync(request, mode, cancellationToken);
                _lastGoodMirror = mode;
                return response;
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

    private async Task<HttpResponseMessage> SendWithModeAsync(
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
            return await SendBoundedAsync(retry, cancellationToken);
        }

        return await SendBoundedAsync(request, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendBoundedAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // 限时只约束到"响应头到手";此后解除,正文读取由调用方控制节奏
        using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headerCts.CancelAfter(TimeSpan.FromSeconds(45));

        var response = await base.SendAsync(request, headerCts.Token);
        headerCts.CancelAfter(Timeout.InfiniteTimeSpan);
        return response;
    }
}
