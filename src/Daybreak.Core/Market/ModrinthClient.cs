using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace Daybreak.Core.Market;

/// <summary>Modrinth 搜索结果项。</summary>
public record ModrinthSearchResult(
    string ProjectId,
    string Slug,
    string Title,
    string Description,
    string Author,
    long Downloads,
    string ProjectType,
    string? IconUrl);

/// <summary>Modrinth 版本的文件。</summary>
public record ModrinthFile(string Url, string Filename, long Size, string? Sha1, bool Primary);

/// <summary>Modrinth 项目版本。</summary>
public record ModrinthVersion(
    string Id,
    string VersionNumber,
    string Name,
    string[] GameVersions,
    string[] Loaders,
    ModrinthFile[] Files,
    DateTimeOffset DatePublished)
{
    public ModrinthFile? PrimaryFile =>
        Files.FirstOrDefault(f => f.Primary) ?? Files.OrderByDescending(f => f.Size).FirstOrDefault();
}

/// <summary>搜索页结果。</summary>
public record ModrinthSearchPage(int TotalHits, IReadOnlyList<ModrinthSearchResult> Hits);

/// <summary>
/// Modrinth API v2 客户端(免 API key)。规范:https://docs.modrinth.com。
/// </summary>
public class ModrinthClient
{
    private const string ApiBase = "https://api.modrinth.com/v2/";
    private readonly HttpClient _http;

    public ModrinthClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Daybreak-Launcher/0.1 (Minecraft launcher)");
    }

    /// <summary>
    /// 搜索资源。projectType: mod / resourcepack / shader / modpack;
    /// gameVersion 与 loader 可为 null(不过滤)。
    /// </summary>
    public async Task<ModrinthSearchPage> SearchAsync(
        string query, string projectType, string? gameVersion = null,
        string? loader = null, int limit = 20, int offset = 0)
    {
        var facets = new List<List<string>>
        {
            new List<string> { $"project_type:{projectType}" },
        };
        if (!string.IsNullOrEmpty(gameVersion))
            facets.Add(new List<string> { $"versions:{gameVersion}" });
        if (!string.IsNullOrEmpty(loader))
            facets.Add(new List<string> { $"categories:{loader}" });

        var url = $"{ApiBase}search?query={Uri.EscapeDataString(query)}" +
                  $"&facets={Uri.EscapeDataString(JsonSerializer.Serialize(facets))}" +
                  $"&index=relevance&limit={limit}&offset={offset}";

        using var document = JsonDocument.Parse(await _http.GetStringAsync(url));
        var root = document.RootElement;
        var hits = new List<ModrinthSearchResult>();

        foreach (var hit in root.GetProperty("hits").EnumerateArray())
        {
            hits.Add(new ModrinthSearchResult(
                hit.GetProperty("project_id").GetString()!,
                hit.GetProperty("slug").GetString() ?? "",
                hit.GetProperty("title").GetString() ?? "",
                hit.GetProperty("description").GetString() ?? "",
                hit.GetProperty("author").GetString() ?? "",
                hit.TryGetProperty("downloads", out var downloads) ? downloads.GetInt64() : 0,
                hit.TryGetProperty("project_type", out var type) ? type.GetString() ?? "mod" : "mod",
                hit.TryGetProperty("icon_url", out var icon) && icon.ValueKind == JsonValueKind.String
                    ? icon.GetString()
                    : null));
        }

        return new ModrinthSearchPage(
            root.TryGetProperty("total_hits", out var total) ? total.GetInt32() : hits.Count, hits);
    }

    /// <summary>获取项目的版本列表,可按游戏版本与加载器过滤。</summary>
    public async Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(
        string projectIdOrSlug, string? gameVersion = null, string? loader = null)
    {
        var url = $"{ApiBase}project/{Uri.EscapeDataString(projectIdOrSlug)}/version";
        var filters = new List<string>();
        if (!string.IsNullOrEmpty(gameVersion))
            filters.Add($"game_versions={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { gameVersion }))}");
        if (!string.IsNullOrEmpty(loader))
            filters.Add($"loaders={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loader }))}");
        if (filters.Count > 0)
            url += "?" + string.Join("&", filters);

        using var document = JsonDocument.Parse(await _http.GetStringAsync(url));
        var versions = new List<ModrinthVersion>();

        foreach (var entry in document.RootElement.EnumerateArray())
            versions.Add(ParseVersionEntry(entry));

        return versions;
    }

    /// <summary>按文件 SHA1 反查 Modrinth 版本(用于 Mod 更新检查);查不到返回 null。</summary>
    public async Task<ModrinthVersion?> GetVersionBySha1Async(string sha1)
    {
        var url = $"{ApiBase}version_file/{Uri.EscapeDataString(sha1)}?algorithm=sha1";
        using var response = await _http.GetAsync(url);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return ParseVersionEntry(document.RootElement);
    }

    private static ModrinthVersion ParseVersionEntry(JsonElement entry)
    {
        var files = new List<ModrinthFile>();
        if (entry.TryGetProperty("files", out var filesElement))
        {
            foreach (var file in filesElement.EnumerateArray())
            {
                files.Add(new ModrinthFile(
                    file.GetProperty("url").GetString()!,
                    file.GetProperty("filename").GetString() ?? "",
                    file.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
                    file.TryGetProperty("hashes", out var hashes) &&
                    hashes.TryGetProperty("sha1", out var sha1)
                        ? sha1.GetString()
                        : null,
                    file.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.True));
            }
        }

        return new ModrinthVersion(
            entry.GetProperty("id").GetString() ?? "",
            entry.GetProperty("version_number").GetString() ?? "",
            entry.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
            ParseStringArray(entry, "game_versions"),
            ParseStringArray(entry, "loaders"),
            files.ToArray(),
            entry.TryGetProperty("date_published", out var date) &&
            DateTimeOffset.TryParse(date.GetString(), out var parsed)
                ? parsed
                : DateTimeOffset.MinValue);
    }

    private static string[] ParseStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var array) ||
            array.ValueKind != JsonValueKind.Array)
            return [];

        return array.EnumerateArray()
            .Select(e => e.GetString() ?? "")
            .Where(s => s.Length > 0)
            .ToArray();
    }
}
