using System.Net.Http;

namespace Daybreak.Core.Game;

/// <summary>
/// 请求级镜像改写:所有经 CmlLib 发出的 Mojang 官方源请求,
/// 在 bmclapi 模式下改道镜像域名(清单 / 库 / 资源全覆盖)。
/// </summary>
public class MirrorHttpHandler : DelegatingHandler
{
    private readonly string _mirror;

    public MirrorHttpHandler(string mirror, HttpMessageHandler? innerHandler = null)
        : base(innerHandler ?? new HttpClientHandler())
    {
        _mirror = mirror;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var rewritten = DownloadMirror.Rewrite(request.RequestUri, _mirror);
        if (!ReferenceEquals(rewritten, request.RequestUri))
            request.RequestUri = rewritten;
        return base.SendAsync(request, cancellationToken);
    }
}
