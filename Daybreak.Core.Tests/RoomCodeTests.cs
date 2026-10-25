using Daybreak.Core.Net;
using Xunit;

namespace Daybreak.Core.Tests;

/// <summary>
/// 房间号与联机参数。
///
/// 这些是"双方必须算出完全一致结果"的纯逻辑 —— 一旦派生规则有歧义或参数拼错，
/// 表现为"房间号没错但就是连不上"，而且极难排查，所以必须有测试锁死。
/// </summary>
public class RoomCodeTests
{
    [Fact]
    public void Generate_ProducesSixDigits_AndValid()
    {
        for (var i = 0; i < 200; i++)
        {
            var code = RoomCode.Generate();

            Assert.Equal(6, code.Length);
            Assert.True(RoomCode.IsValid(code), $"生成了非法房间号:{code}");
            Assert.NotEqual('0', code[0]);
        }
    }

    [Fact]
    public void Generate_IsNotAlwaysTheSame()
    {
        // 200 次里至少要有多个不同值(防止误用固定种子的 Random)
        var codes = Enumerable.Range(0, 200).Select(_ => RoomCode.Generate()).Distinct().Count();
        Assert.True(codes > 50, $"随机性可疑:200 次只产生 {codes} 个不同房间号");
    }

    [Theory]
    [InlineData("123456", true)]
    [InlineData("999999", true)]
    [InlineData("100000", true)]
    [InlineData("012345", false)]   // 首位不能为 0
    [InlineData("12345", false)]    // 位数不足
    [InlineData("1234567", false)]  // 位数过多
    [InlineData("12345a", false)]   // 含非数字
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValid_ChecksFormat(string? code, bool expected) =>
        Assert.Equal(expected, RoomCode.IsValid(code));

    [Theory]
    [InlineData(" 123456 ", "123456")]
    [InlineData("123-456", "123456")]
    [InlineData("123 456", "123456")]
    public void Normalize_StripsSpacesAndSeparators(string input, string expected) =>
        Assert.Equal(expected, RoomCode.Normalize(input));

    [Theory]
    [InlineData("12345")]
    [InlineData("012345")]
    [InlineData("abcdef")]
    [InlineData("")]
    public void Normalize_RejectsInvalid(string input) =>
        Assert.Null(RoomCode.Normalize(input));

    [Fact]
    public void Derive_IsDeterministic()
    {
        // 这是联机的根本前提:双方输入同一房间号必须得到完全相同的网络名与密钥
        var a = RoomCode.Derive("654321");
        var b = RoomCode.Derive("654321");

        Assert.Equal(a.NetworkName, b.NetworkName);
        Assert.Equal(a.NetworkSecret, b.NetworkSecret);
    }

    [Fact]
    public void Derive_DifferentCodes_ProduceDifferentNetworks()
    {
        var a = RoomCode.Derive("111111");
        var b = RoomCode.Derive("222222");

        Assert.NotEqual(a.NetworkName, b.NetworkName);
        Assert.NotEqual(a.NetworkSecret, b.NetworkSecret);
    }

    [Fact]
    public void Derive_SecretDoesNotContainRawCode()
    {
        // 密钥由 HMAC 派生，不能把房间号原样拼进去 ——
        // 否则抓包/看日志就能直接读出房间号
        var credentials = RoomCode.Derive("778899");

        Assert.DoesNotContain("778899", credentials.NetworkSecret);
        Assert.DoesNotContain("778899", credentials.NetworkName, StringComparison.Ordinal);
    }

    [Fact]
    public void Derive_ProducesReasonableLengths()
    {
        var c = RoomCode.Derive("456789");

        // EasyTier 对名称/密钥没有严格长度限制，但太短不安全、太长命令行不便
        Assert.InRange(c.NetworkName.Length, 8, 32);
        Assert.InRange(c.NetworkSecret.Length, 32, 128);
        // 密钥只应包含安全字符(避免命令行转义问题)
        Assert.Matches("^[0-9a-f]+$", c.NetworkSecret);
    }

    [Fact]
    public void Derive_RejectsInvalidCode() =>
        Assert.Throws<ArgumentException>(() => RoomCode.Derive("12345"));

    [Fact]
    public void HostAddress_PointsToFixedHostIp() =>
        Assert.Equal("10.144.144.1:25565", RoomCode.Derive("123456").HostAddress);
}

/// <summary>easytier-core 命令行参数构造。</summary>
public class EasyTierArgumentsTests
{
    /// <summary>测试用假中继地址。真实地址不进 git(在 easytier/relay.txt 里)。</summary>
    private const string TestRelay = "relay.example.invalid:11010";

    [Fact]
    public void HostArguments_FixVirtualIp_AndIncludeOneRelay()
    {
        var cred = RoomCode.Derive("123456");
        var args = EasyTierManager.BuildArguments(cred, isHost: true, TestRelay);

        // 房主必须用固定 IP，好友才能连
        var ipIndex = args.IndexOf("--ipv4");
        Assert.True(ipIndex >= 0, "房主应带 --ipv4");
        Assert.Equal(RoomCredentials.HostIp, args[ipIndex + 1]);

        // 房主不能用 dhcp(否则 IP 会变，好友连不上)
        Assert.DoesNotContain("--dhcp", args);

        Assert.Contains("--network-name", args);
        Assert.Contains(cred.NetworkName, args);
        Assert.Contains("--network-secret", args);
        Assert.Contains(cred.NetworkSecret, args);

        // 关键回归:只能有一个 -e，多一个 easytier-core 会静默退出
        Assert.Equal(1, args.Count(a => a == "-e"));
        Assert.Contains($"tcp://{TestRelay}", args);
    }

    [Fact]
    public void GuestArguments_UseDhcp_AndIncludeOneRelay()
    {
        var cred = RoomCode.Derive("123456");
        var args = EasyTierManager.BuildArguments(cred, isHost: false, TestRelay);

        // 加入者用 dhcp，避免多人同房间时 IP 冲突
        Assert.Contains("--dhcp", args);
        Assert.DoesNotContain("--ipv4", args);

        Assert.Equal(1, args.Count(a => a == "-e"));
        Assert.Contains($"tcp://{TestRelay}", args);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void BuildArguments_WithoutRelay_ThrowsClearError(string? relay)
    {
        // 没配中继时必须明确报错，而不是拼出一个连不上的地址
        var cred = RoomCode.Derive("123456");
        Assert.Throws<ArgumentException>(
            () => EasyTierManager.BuildArguments(cred, isHost: true, relay!));
    }

    [Fact]
    public void Arguments_NeverContainExplicitTunFlag()
    {
        // 我们依赖默认的 TUN 行为。
        // 如果误加 --no-tun，本机就没有虚拟网卡，游戏连不上虚拟 IP。
        var cred = RoomCode.Derive("123456");

        foreach (var isHost in new[] { true, false })
        {
            var args = EasyTierManager.BuildArguments(cred, isHost, TestRelay);
            Assert.DoesNotContain("--no-tun", args);
            Assert.DoesNotContain("--use-smoltcp", args);
        }
    }
}