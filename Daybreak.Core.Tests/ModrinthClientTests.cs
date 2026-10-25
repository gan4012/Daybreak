using System.Net;
using System.Text;
using Daybreak.Core.Market;
using Xunit;

namespace Daybreak.Core.Tests;

/// <summary>Modrinth 客户端离线桩测试(不打真实网络)。</summary>
public class ModrinthClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public string? LastUrl;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUrl = request.RequestUri!.AbsoluteUri;
            return Task.FromResult(responder(request));
        }
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Search_ParsesHitsAndBuildsFacets()
    {
        string? captured = null;
        var handler = new StubHandler(request =>
        {
            captured = request.RequestUri!.ToString();
            return Json("""
                {
                  "total_hits": 2,
                  "hits": [
                    { "project_id": "id1", "slug": "jei", "title": "Just Enough Items",
                      "description": "item viewer", "author": "mezz", "downloads": 100,
                      "project_type": "mod", "icon_url": null },
                    { "project_id": "id2", "slug": "sodium", "title": "Sodium",
                      "description": "rendering", "author": "jellysquid", "downloads": 200,
                      "project_type": "mod" }
                  ]
                }
                """);
        });
        var client = new ModrinthClient(new HttpClient(handler));

        var page = await client.SearchAsync("jei", "mod", "1.20.1", "fabric");

        Assert.Equal(2, page.TotalHits);
        Assert.Equal("jei", page.Hits[0].Slug);
        Assert.Equal("mezz", page.Hits[0].Author);
        Assert.Null(page.Hits[0].IconUrl);
        Assert.Equal(200, page.Hits[1].Downloads);

        // facets 参数解码后应包含三类过滤条件
        var query = new Uri(captured).Query;
        var decoded = Uri.UnescapeDataString(query);
        Assert.Contains("facets=", decoded);
        Assert.Contains("project_type:mod", decoded);
        Assert.Contains("versions:1.20.1", decoded);
        Assert.Contains("categories:fabric", decoded);
    }

    [Fact]
    public async Task GetVersions_ParsesFilesAndPrimary()
    {
        var handler = new StubHandler(_ => Json("""
            [
              {
                "id": "ver1", "version_number": "1.0.0", "name": "First release",
                "game_versions": ["1.20.1"], "loaders": ["fabric"],
                "date_published": "2024-01-01T00:00:00Z",
                "files": [
                  { "url": "https://cdn/mod.jar", "filename": "mod.jar", "size": 1024,
                    "hashes": { "sha1": "abc" }, "primary": true }
                ]
              }
            ]
            """));
        var client = new ModrinthClient(new HttpClient(handler));

        var versions = await client.GetVersionsAsync("jei", "1.20.1", "fabric");

        var version = Assert.Single(versions);
        Assert.Equal("1.0.0", version.VersionNumber);
        Assert.Equal(["1.20.1"], version.GameVersions);
        var file = version.PrimaryFile!;
        Assert.Equal("https://cdn/mod.jar", file.Url);
        Assert.Equal(1024, file.Size);
        Assert.Equal("abc", file.Sha1);
        Assert.True(file.Primary);
    }
}
