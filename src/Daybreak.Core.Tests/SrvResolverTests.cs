using Daybreak.Core.Server;
using Xunit;
using Xunit.Abstractions;

namespace Daybreak.Core.Tests;

/// <summary>
/// SRV 记录解析的集成测试(需要 DNS)。
///
/// 这个测试的由来:实测发现 2b2t.org 把真实入口放在 SRV 记录里
/// (`_minecraft._tcp.2b2t.org` → `connect.2b2t.org:25565`),
/// 不做 SRV 解析就会连到前置 CDN 上然后超时——用户只看到"查询失败",完全不知道为什么。
/// </summary>
[Trait("Category", "Integration")]
public class SrvResolverTests
{
    private readonly ITestOutputHelper _output;

    public SrvResolverTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task ResolveMinecraftAsync_FindsRealSrvRecord()
    {
        // 2b2t 是已知有 SRV 记录的服务器
        var srv = await SrvResolver.ResolveMinecraftAsync("2b2t.org", TimeSpan.FromSeconds(8));

        Assert.NotNull(srv);
        _output.WriteLine($"2b2t.org → {srv.Target}:{srv.Port} (priority={srv.Priority}, weight={srv.Weight})");

        Assert.Equal(25565, srv.Port);
        Assert.Contains("2b2t.org", srv.Target, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResolveMinecraftAsync_NoRecord_ReturnsNull()
    {
        // 没有 SRV 记录的域名要安静返回 null,让调用方回退直连
        var srv = await SrvResolver.ResolveMinecraftAsync(
            "daybreak-no-srv-record.invalid", TimeSpan.FromSeconds(6));

        Assert.Null(srv);
    }

    [Fact]
    public async Task ResolveMinecraftAsync_IpAddress_ReturnsNullWithoutQuerying()
    {
        // 直接给 IP 时不该去查 SRV
        var srv = await SrvResolver.ResolveMinecraftAsync("127.0.0.1", TimeSpan.FromSeconds(3));
        Assert.Null(srv);
    }

    [Fact]
    public async Task QueryAsync_ViaSrv_ReachesServerThatDirectConnectCannot()
    {
        // 端到端:带 SRV 的服务器应该能被查询到(证明 SRV 链路确实生效)
        var status = await ServerListPing.QueryAsync("2b2t.org", 25565, TimeSpan.FromSeconds(25));

        if (!status.Online)
            _output.WriteLine($"2b2t.org 查询失败(可能是网络环境问题):{status.Error}");

        _output.WriteLine($"2b2t.org: online={status.Online} 人数={status.PlayerDisplay} " +
                          $"版本={status.VersionName} 延迟={status.LatencyMs}ms MOTD={status.Motd}");

        Assert.True(status.Online, $"经 SRV 仍无法查询 2b2t.org:{status.Error}");
        Assert.True(status.MaxPlayers > 0, "最大人数解析异常");
    }
}