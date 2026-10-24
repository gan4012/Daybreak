using System.Net;
using System.Text;
using Daybreak.Core.Auth;
using Xunit;

namespace Daybreak.Core.Tests;

/// <summary>Yggdrasil 协议客户端的离线桩测试(不打真实网络)。</summary>
public class YggdrasilClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(responder(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body, string? aliLocation = null)
    {
        var response = new HttpResponseMessage(code)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (aliLocation is not null)
            response.Headers.Add("X-Authlib-Injector-API-Location", aliLocation);
        return response;
    }

    private const string SuccessBody = """
        {
          "accessToken": "token-abc",
          "clientToken": "client-123",
          "availableProfiles": [
            { "id": "uuid-1", "name": "Alice" },
            { "id": "uuid-2", "name": "Bob" }
          ],
          "selectedProfile": { "id": "uuid-1", "name": "Alice" }
        }
        """;

    [Fact]
    public async Task Discover_FollowsAliHeaderAndReadsServerName()
    {
        var handler = new StubHandler(request =>
        {
            var body = request.RequestUri!.ToString().Contains("real")
                ? """{"meta":{"serverName":"测试服"}}"""
                : "{}";
            return request.RequestUri.ToString().Contains("real")
                ? Json(HttpStatusCode.OK, body)
                : Json(HttpStatusCode.OK, body, "https://real.example.com/api/yggdrasil");
        });
        var client = new YggdrasilClient(new HttpClient(handler));

        var server = await client.DiscoverAsync("example.com");

        Assert.Equal("https://real.example.com/api/yggdrasil/", server.ApiRoot);
        Assert.Equal("测试服", server.ServerName);
    }

    [Fact]
    public async Task Authenticate_SendsSpecPayloadAndParsesSelectedProfile()
    {
        string? sentBody = null;
        var handler = new StubHandler(request =>
        {
            sentBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json(HttpStatusCode.OK, SuccessBody);
        });
        var client = new YggdrasilClient(new HttpClient(handler));

        var session = await client.AuthenticateAsync(
            "https://api.example.com/", "alice@example.com", "pass", "client-123");

        Assert.Equal("token-abc", session.AccessToken);
        Assert.Equal("uuid-1", session.Uuid);
        Assert.Equal("Alice", session.Name);
        Assert.Contains("\"clientToken\":\"client-123\"", sentBody);
        Assert.Contains("MinecraftAgent", sentBody);
        Assert.StartsWith("https://api.example.com/authserver/authenticate",
            handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task Authenticate_ServerError_RaisesWithMessage()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.Forbidden,
            """{"error":"ForbiddenOperationException","errorMessage":"Invalid credentials"}"""));
        var client = new YggdrasilClient(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<YggdrasilException>(
            () => client.AuthenticateAsync("https://api.example.com/", "u", "p", "c"));
        Assert.Equal("Invalid credentials", ex.Message);
    }

    [Fact]
    public async Task Validate_ReturnsTrueOn204AndFalseOtherwise()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = new YggdrasilClient(new HttpClient(handler));
        Assert.True(await client.ValidateAsync("https://api.example.com/", "t", "c"));

        var failing = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var client2 = new YggdrasilClient(new HttpClient(failing));
        Assert.False(await client2.ValidateAsync("https://api.example.com/", "t", "c"));
    }

    [Fact]
    public async Task Refresh_ParsesNewToken()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK,
            """{"accessToken":"new-token","clientToken":"client-123","selectedProfile":{"id":"uuid-1","name":"Alice"}}"""));
        var client = new YggdrasilClient(new HttpClient(handler));

        var session = await client.RefreshAsync(
            "https://api.example.com/", "old-token", "client-123", "uuid-1", "Alice");

        Assert.Equal("new-token", session.AccessToken);
    }
}
