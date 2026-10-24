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
    public void Rewrite_BmclapiMode_MapsKnownHosts(string source, string expected)
    {
        var rewritten = DownloadMirror.Rewrite(new Uri(source), DownloadMirror.Bmclapi);
        Assert.Equal(expected, rewritten!.ToString());
    }

    [Fact]
    public void Rewrite_BmclapiMode_KeepsUnknownHosts()
    {
        var uri = new Uri("https://meta.fabricmc.net/v2/versions/loader");
        Assert.Same(uri, DownloadMirror.Rewrite(uri, DownloadMirror.Bmclapi));
    }
}
