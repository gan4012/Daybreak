using Daybreak.Core.Game;
using Xunit;

namespace Daybreak.Core.Tests;

public class DownloadMirrorTests
{
    [Theory]
    [InlineData("official")]
    [InlineData("bmclapi")]
    public void IsValid_AcceptsKnownMirrors(string mirror) =>
        Assert.True(DownloadMirror.IsValid(mirror));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tpyge")]
    public void IsValid_RejectsUnknownMirrors(string? mirror) =>
        Assert.False(DownloadMirror.IsValid(mirror));

    [Fact]
    public void Rewrite_OfficialMode_ReturnsSourceUnchanged()
    {
        var uri = new Uri("https://launchermeta.mojang.com/mc/game/version_manifest_v2.json");
        Assert.Same(uri, DownloadMirror.Rewrite(uri, DownloadMirror.Official));
    }

    [Theory]
    [InlineData("https://launchermeta.mojang.com/mc/game/version_manifest_v2.json",
                "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json")]
    [InlineData("https://piston-meta.mojang.com/v1/packages/abc/1.21.json",
                "https://bmclapi2.bangbang93.com/v1/packages/abc/1.21.json")]
    [InlineData("https://libraries.minecraft.net/net/minecraft/a.jar",
                "https://bmclapi2.bangbang93.com/maven/net/minecraft/a.jar")]
    [InlineData("https://resources.download.minecraft.net/ab/abcd1234",
                "https://bmclapi2.bangbang93.com/assets/ab/abcd1234")]
    // 加载器 Maven:国内直连 neoforged 会在 TLS 阶段超时,必须能被改写
    [InlineData("https://maven.neoforged.net/releases/net/neoforged/neoforge/21.1.72/neoforge-21.1.72-installer.jar",
                "https://bmclapi2.bangbang93.com/maven/net/neoforged/neoforge/21.1.72/neoforge-21.1.72-installer.jar")]
    [InlineData("https://maven.minecraftforge.net/net/minecraftforge/forge/1.20.1-47.4.28/forge-1.20.1-47.4.28-installer.jar",
                "https://bmclapi2.bangbang93.com/maven/net/minecraftforge/forge/1.20.1-47.4.28/forge-1.20.1-47.4.28-installer.jar")]
    [InlineData("https://files.minecraftforge.net/maven/net/minecraftforge/forge/maven-metadata.xml",
                "https://bmclapi2.bangbang93.com/maven/net/minecraftforge/forge/maven-metadata.xml")]
    public void Rewrite_BmclapiMode_MapsKnownHosts(string source, string expected)
    {
        var rewritten = DownloadMirror.Rewrite(new Uri(source), DownloadMirror.Bmclapi);
        Assert.Equal(expected, rewritten!.ToString());
    }

    /// <summary>
    /// neoforged 的 releases/ 段要去掉(BMCLAPI 的 /maven/ 已包含该层级),
    /// 若前缀匹配顺序写反会得到 .../maven/releases/net/... 这种错误地址。
    /// </summary>
    [Fact]
    public void Rewrite_NeoForgeMaven_DropsReleasesSegment()
    {
        var rewritten = DownloadMirror.Rewrite(
            new Uri("https://maven.neoforged.net/releases/net/neoforged/forge/1.20.1-47.1.0/forge-1.20.1-47.1.0-installer.jar"),
            DownloadMirror.Bmclapi);

        Assert.Equal(
            "https://bmclapi2.bangbang93.com/maven/net/neoforged/forge/1.20.1-47.1.0/forge-1.20.1-47.1.0-installer.jar",
            rewritten!.ToString());
        Assert.DoesNotContain("releases", rewritten.ToString());
    }

    [Fact]
    public void Rewrite_BmclapiMode_KeepsUnknownHosts()
    {
        var uri = new Uri("https://meta.fabricmc.net/v2/versions/loader");
        Assert.Same(uri, DownloadMirror.Rewrite(uri, DownloadMirror.Bmclapi));
    }
}
