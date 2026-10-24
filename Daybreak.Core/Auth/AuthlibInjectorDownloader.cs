using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Daybreak.Core.Auth;

/// <summary>
/// authlib-injector 运行库下载器(-javaagent 注入所需的 jar)。
/// 包托管在 GitHub Releases,墙内优先走 BMCLAPI 镜像,失败时回退 gh-proxy。
/// </summary>
public class AuthlibInjectorDownloader(HttpClient http)
{
    public const string BmclapiLatestUrl =
        "https://bmclapi2.bangbang93.com/mirrors/authlib-injector/artifact/latest.json";

    public AuthlibInjectorDownloader() : this(new HttpClient()) { }

    /// <summary>获取(必要时下载)authlib-injector.jar,返回其完整路径。</summary>
    public async Task<string> EnsureJarAsync(string directory, string? mirror = null)
    {
        Directory.CreateDirectory(directory);
        var targetPath = Path.Combine(directory, "authlib-injector.jar");

        var (url, sha256) = await ResolveLatestAsync(mirror);
        if (File.Exists(targetPath))
        {
            if (sha256 is null || ComputeSha256(targetPath) == sha256)
                return targetPath;
            // 版本不匹配,重新下载
            File.Delete(targetPath);
        }

        var data = await http.GetByteArrayAsync(url);
        await File.WriteAllBytesAsync(targetPath, data);

        if (sha256 is not null && ComputeSha256(targetPath) != sha256)
        {
            File.Delete(targetPath);
            throw new InvalidOperationException("authlib-injector.jar SHA256 校验失败");
        }

        return targetPath;
    }

    /// <summary>解析最新构建的下载地址与校验值;主源失败时走 gh-proxy 兜底。</summary>
    private async Task<(string Url, string? Sha256)> ResolveLatestAsync(string? mirror)
    {
        var mirrorMode = mirror == "bmclapi";
        try
        {
            return await FetchLatestAsync(BmclapiLatestUrl);
        }
        catch (Exception) when (!mirrorMode)
        {
            // BMCLAPI 不可达时,经 gh-proxy 访问 GitHub Releases 元数据
            var fallback = "https://gh-proxy.com/" +
                "https://api.github.com/repos/yushijinhun/authlib-injector/releases/latest";
            var url = await FetchGithubLatestAsync(fallback);
            return (url, null);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("获取 authlib-injector 元数据失败", ex);
        }
    }

    private async Task<(string Url, string? Sha256)> FetchLatestAsync(string url)
    {
        var json = await http.GetStringAsync(url);
        return ParseLatest(json);
    }

    private static (string Url, string? Sha256) ParseLatest(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var downloadUrl = root.GetProperty("download_url").GetString()
            ?? throw new InvalidOperationException("latest.json 缺少 download_url");
        var sha256 = root.TryGetProperty("checksums", out var checksums) &&
                     checksums.TryGetProperty("sha256", out var sha)
            ? sha.GetString()
            : null;
        return (downloadUrl, sha256);
    }

    private async Task<string> FetchGithubLatestAsync(string apiUrl)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        request.Headers.UserAgent.ParseAdd("Daybreak-Launcher");
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(json);
        foreach (var asset in document.RootElement.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) &&
                name.StartsWith("authlib-injector", StringComparison.OrdinalIgnoreCase))
                return asset.GetProperty("browser_download_url").GetString()!;
        }
        throw new InvalidOperationException("Release 中未找到 jar 资产");
    }

    private static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
