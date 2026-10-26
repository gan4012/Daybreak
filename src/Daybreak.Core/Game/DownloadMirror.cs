using System.Net.Http;

namespace Daybreak.Core.Game;

/// <summary>
/// 下载镜像定义与 URL 改写。BMCLAPI 域名映射表来自其官方文档,
/// 覆盖版本元数据 / 库文件 / 资源文件,以及 Forge / NeoForge 的 Maven 仓库。
///
/// 加载器 Maven 也必须走镜像:实测国内直连 maven.neoforged.net 会在 TLS 阶段超时
/// (TCP 可连、HTTPS 请求挂死),而 BMCLAPI 的 /maven/ 完整镜像了 neoforged 与 forge
/// (已验证 neoforge 安装器 jar 可取,3.5MB)。
/// CmlLib 的 Forge/NeoForge 安装器硬编码官方 Maven 地址,
/// 这里靠 ResilientHttpHandler 的镜像降级重试把它们改写到 BMCLAPI。
/// </summary>
public static class DownloadMirror
{
    public const string Official = "official";
    public const string Bmclapi = "bmclapi";

    /// <summary>注意顺序:更长的前缀必须排在前面,否则会被短前缀抢先匹配。</summary>
    private static readonly (string From, string To)[] BmclapiRewrites =
    [
        ("https://launchermeta.mojang.com/", "https://bmclapi2.bangbang93.com/"),
        ("https://piston-meta.mojang.com/", "https://bmclapi2.bangbang93.com/"),
        ("https://piston-data.mojang.com/", "https://bmclapi2.bangbang93.com/"),
        ("https://libraries.minecraft.net/", "https://bmclapi2.bangbang93.com/maven/"),
        ("https://resources.download.minecraft.net/", "https://bmclapi2.bangbang93.com/assets/"),
        // Forge / NeoForge Maven(加载器安装器的 jar 与依赖都在这里)
        ("https://maven.neoforged.net/releases/", "https://bmclapi2.bangbang93.com/maven/"),
        ("https://maven.neoforged.net/", "https://bmclapi2.bangbang93.com/maven/"),
        ("https://maven.minecraftforge.net/", "https://bmclapi2.bangbang93.com/maven/"),
        ("https://files.minecraftforge.net/maven/", "https://bmclapi2.bangbang93.com/maven/"),
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
