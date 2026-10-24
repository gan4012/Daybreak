using System.Net.Http;

namespace Daybreak.Core.Game;

/// <summary>
/// 下载镜像定义与 URL 改写。BMCLAPI 域名映射表来自其官方文档,
/// 覆盖版本元数据 / 库文件 / 资源文件三类 Mojang 域名。
/// </summary>
public static class DownloadMirror
{
    public const string Official = "official";
    public const string Bmclapi = "bmclapi";

    private static readonly (string From, string To)[] BmclapiRewrites =
    [
        ("https://launchermeta.mojang.com/", "https://bmclapi2.bangbang93.com/"),
        ("https://piston-meta.mojang.com/", "https://bmclapi2.bangbang93.com/"),
        ("https://piston-data.mojang.com/", "https://bmclapi2.bangbang93.com/"),
        ("https://libraries.minecraft.net/", "https://bmclapi2.bangbang93.com/maven/"),
        ("https://resources.download.minecraft.net/", "https://bmclapi2.bangbang93.com/assets/"),
    ];

    public static bool IsValid(string? mirror) => mirror is Official or Bmclapi;

    /// <summary>把 Mojang 官方源 URL 改写为镜像源 URL;非官方域名原样返回。</summary>
    public static Uri? Rewrite(Uri? source, string mirror)
    {
        if (source is null || mirror != Bmclapi)
            return source;

        var raw = source.ToString();
        foreach (var (from, to) in BmclapiRewrites)
        {
            if (raw.StartsWith(from, StringComparison.OrdinalIgnoreCase))
                return new Uri(to + raw[from.Length..]);
        }

        return source;
    }
}
