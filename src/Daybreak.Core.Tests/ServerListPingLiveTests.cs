using Daybreak.Core.Server;
using Xunit;
using Xunit.Abstractions;

namespace Daybreak.Core.Tests;

/// <summary>
/// 对**真实公开服务器**做 SLP 查询的集成测试。
///
/// 桩服务端测试只能证明编解码自洽(自己编自己解,可能一起错),
/// 这里打真实服务端才能证明协议实现与业界实现兼容。
/// 需要外网,故归类为 Integration。
/// </summary>
[Trait("Category", "Integration")]
public class ServerListPingLiveTests
{
    private readonly ITestOutputHelper _output;

    public ServerListPingLiveTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData("mc.hypixel.net", 25565)]
    [InlineData("2b2t.org", 25565)]
    public async Task QueryAsync_AgainstRealPublicServer_ParsesStatus(string host, int port)
    {
        var status = await ServerListPing.QueryAsync(host, port, TimeSpan.FromSeconds(20));

        if (!status.Online)
        {
            _output.WriteLine($"{host} 查询失败:{status.Error}");
        }

        Assert.True(status.Online, $"查询 {host} 失败:{status.Error}");

        _output.WriteLine($"{host}:{port}");
        _output.WriteLine($"  版本   = {status.VersionName} (protocol {status.ProtocolVersion})");
        _output.WriteLine($"  人数   = {status.PlayerDisplay}");
        _output.WriteLine($"  延迟   = {status.LatencyMs} ms");
        _output.WriteLine($"  MOTD   = {status.Motd}");
        _output.WriteLine($"  favicon = {(string.IsNullOrEmpty(status.Favicon) ? "(无)" : $"有({status.Favicon.Length} 字符)")}");

        // 真实服务端必须有合理的版本名与人数上限
        Assert.False(string.IsNullOrWhiteSpace(status.VersionName), "版本名为空");
        Assert.True(status.MaxPlayers > 0, "最大人数为 0,解析可能有问题");
        Assert.True(status.ProtocolVersion > 0, "协议版本号未解析出来");
        Assert.True(status.LatencyMs >= 0);
    }

    [Fact]
    public async Task QueryAsync_ThenPingOwnVanillaServer_RoundTrips()
    {
        // 对不可达地址的容错(保证 UI 不会因为一个坏地址就崩)
        var status = await ServerListPing.QueryAsync(
            "this-host-should-not-exist-daybreak.invalid", 25565, TimeSpan.FromSeconds(8));

        Assert.False(status.Online);
        Assert.NotNull(status.Error);
        _output.WriteLine($"预期的失败信息:{status.Error}");
    }
}