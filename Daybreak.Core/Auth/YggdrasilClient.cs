using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Daybreak.Core.Auth;

/// <summary>发现并解析后的 Yggdrasil 认证服务器信息。</summary>
public record YggdrasilServer(string ApiRoot, string ServerName);

/// <summary>Yggdrasil 登录结果(对应一个 Minecraft 角色档案)。</summary>
public record YggdrasilSession(string AccessToken, string Uuid, string Name, string ClientToken);

/// <summary>Yggdrasil 服务器返回的业务错误(authenticate/refresh 等)。</summary>
public class YggdrasilException(string message) : Exception(message);

/// <summary>
/// authlib-injector Yggdrasil 协议客户端(自研实现,依据 bs-community/yggdrasil-api 规范;
/// 不参考 authlib-injector 的 AGPL 代码)。HttpClient 可注入以便测试打桩。
/// </summary>
public class YggdrasilClient(HttpClient http)
{
    public YggdrasilClient() : this(new HttpClient()) { }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// ALI 发现:输入用户给的服务器地址(主页或 API 根),
    /// 依据 X-Authlib-Injector-API-Location 响应头重定向到真正的 API 根,并读取服务器名。
    /// </summary>
    public async Task<YggdrasilServer> DiscoverAsync(string userInput)
    {
        var url = NormalizeInput(userInput);

        // 最多跟随 3 次 ALI 重定向(规范允许指向更规范的 API 地址)
        for (var i = 0; i < 3; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await http.SendAsync(request);
            response.EnsureSuccessStatusCode();

            if (response.Headers.TryGetValues("X-Authlib-Injector-API-Location", out var locations))
            {
                var location = locations.FirstOrDefault();
                if (!string.IsNullOrEmpty(location) &&
                    !string.Equals(location, url, StringComparison.OrdinalIgnoreCase))
                {
                    url = new Uri(new Uri(url), location).ToString();
                    continue;
                }
            }

            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync());
            var serverName =
                document.RootElement.TryGetProperty("meta", out var meta) &&
                meta.TryGetProperty("serverName", out var nameElement)
                    ? nameElement.GetString()
                    : null;
            return new YggdrasilServer(url.TrimEnd('/') + "/", serverName ?? url);
        }

        throw new YggdrasilException("ALI 重定向次数过多");
    }

    /// <summary>账号密码登录,返回 accessToken 与所选档案。</summary>
    public async Task<YggdrasilSession> AuthenticateAsync(
        string apiRoot, string username, string password, string clientToken)
    {
        var payload = new
        {
            agent = new { name = "MinecraftAgent", version = 1 },
            username,
            password,
            clientToken,
        };
        using var document = await PostAsync(apiRoot + "authserver/authenticate", payload);

        CheckError(document.RootElement);
        var root = document.RootElement;
        var accessToken = root.GetProperty("accessToken").GetString()
            ?? throw new YggdrasilException("服务器未返回 accessToken");

        if (root.TryGetProperty("selectedProfile", out var selected) &&
            selected.ValueKind == JsonValueKind.Object)
        {
            return new YggdrasilSession(
                accessToken, selected.GetProperty("id").GetString()!, selected.GetProperty("name").GetString()!, clientToken);
        }

        // 无 selectedProfile:角色由用户在服务器端选择,此处取第一个可用档案
        if (root.TryGetProperty("availableProfiles", out var profiles) &&
            profiles.ValueKind == JsonValueKind.Array && profiles.GetArrayLength() > 0)
        {
            var first = profiles[0];
            return new YggdrasilSession(
                accessToken, first.GetProperty("id").GetString()!, first.GetProperty("name").GetString()!, clientToken);
        }

        throw new YggdrasilException("该账号在服务器上没有可用的角色档案");
    }

    /// <summary>校验 accessToken 是否仍然有效。</summary>
    public async Task<bool> ValidateAsync(string apiRoot, string accessToken, string clientToken)
    {
        try
        {
            var payload = new { accessToken, clientToken };
            using var response = await PostRawAsync(apiRoot + "authserver/validate", payload);
            return response.StatusCode == HttpStatusCode.NoContent;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    /// <summary>刷新 accessToken(令牌失效或主动续期)。</summary>
    public async Task<YggdrasilSession> RefreshAsync(
        string apiRoot, string accessToken, string clientToken, string profileId, string profileName)
    {
        var payload = new
        {
            accessToken,
            clientToken,
            selectedProfile = new { id = profileId, name = profileName },
        };
        using var document = await PostAsync(apiRoot + "authserver/refresh", payload);
        CheckError(document.RootElement);
        var root = document.RootElement;
        var newToken = root.GetProperty("accessToken").GetString()
            ?? throw new YggdrasilException("服务器未返回 accessToken");
        return new YggdrasilSession(newToken, profileId, profileName, clientToken);
    }

    private static string NormalizeInput(string input)
    {
        input = input.Trim();
        if (!input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            input = "https://" + input;
        return input.TrimEnd('/') + "/";
    }

    private async Task<JsonDocument> PostAsync(string url, object payload)
    {
        using var response = await PostRawAsync(url, payload);
        var body = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrEmpty(body))
            throw new YggdrasilException($"服务器返回空响应(HTTP {(int)response.StatusCode})");
        return JsonDocument.Parse(body);
    }

    private async Task<HttpResponseMessage> PostRawAsync(string url, object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await http.PostAsync(url, content);
    }

    private static void CheckError(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("errorMessage", out var error))
        {
            var message = error.GetString();
            if (!string.IsNullOrEmpty(message))
                throw new YggdrasilException(message);
        }
    }
}
