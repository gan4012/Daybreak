using Daybreak.Core.Game;
using Daybreak.Core.Server;
using Xunit;

namespace Daybreak.Core.Tests;

/// <summary>
/// 局域网广播报文解析。
/// 报文格式是 Minecraft 客户端固定的 `[MOTD]名称[/MOTD][AD]端口[/AD]`,
/// 解析错了会导致"明明开了局域网却扫不到",而且没有任何报错线索。
/// </summary>
public class LanScannerTests
{
    [Fact]
    public void ParseBroadcast_StandardMessage()
    {
        var world = LanScanner.ParseBroadcast("[MOTD]小明的世界[/MOTD][AD]54321[/AD]", "192.168.1.7");

        Assert.NotNull(world);
        Assert.Equal("小明的世界", world.Motd);
        Assert.Equal(54321, world.Port);
        Assert.Equal("192.168.1.7", world.Address);
    }

    [Fact]
    public void ParseBroadcast_MultilineMotd_IsFlattened()
    {
        // 房间名可能带换行,展示时必须拍平
        var world = LanScanner.ParseBroadcast("[MOTD]第一行\n第二行[/MOTD][AD]25565[/AD]", "10.0.0.5");

        Assert.NotNull(world);
        Assert.Equal("第一行 第二行", world.Motd);
        Assert.DoesNotContain("\n", world.Motd);
    }

    [Fact]
    public void ParseBroadcast_EmptyMotd_FallsBackToPlaceholder()
    {
        var world = LanScanner.ParseBroadcast("[MOTD][/MOTD][AD]25565[/AD]", "192.168.0.2");

        Assert.NotNull(world);
        Assert.Equal("(未命名世界)", world.Motd);
    }

    [Theory]
    [InlineData("")]
    [InlineData("随便一段不是协议格式的文本")]
    [InlineData("[MOTD]有名称但没端口[/MOTD]")]
    [InlineData("[AD]25565[/AD]")]                       // 缺 MOTD 但端口合法 → 仍应解析(容错)
    public void ParseBroadcast_MalformedInput_HandledGracefully(string input)
    {
        var world = LanScanner.ParseBroadcast(input, "192.168.1.1");

        if (input.Contains("[AD]25565"))
        {
            // 端口信息足够定位世界,名称缺失可以兜底
            Assert.NotNull(world);
            Assert.Equal(25565, world.Port);
        }
        else
        {
            Assert.Null(world);
        }
    }

    [Theory]
    [InlineData("[MOTD]x[/MOTD][AD]0[/AD]")]
    [InlineData("[MOTD]x[/MOTD][AD]70000[/AD]")]
    [InlineData("[MOTD]x[/MOTD][AD]abc[/AD]")]
    public void ParseBroadcast_InvalidPort_ReturnsNull(string input)
    {
        Assert.Null(LanScanner.ParseBroadcast(input, "192.168.1.1"));
    }

    [Fact]
    public void LanWorld_IsAlive_ExpiresAfterFiveSeconds()
    {
        var fresh = new LanWorld("t", 25565, "127.0.0.1", DateTime.Now);
        Assert.True(fresh.IsAlive);

        // 超过 5 秒没再收到广播,认为世界已关闭
        var stale = new LanWorld("t", 25565, "127.0.0.1", DateTime.Now.AddSeconds(-6));
        Assert.False(stale.IsAlive);
    }

    [Fact]
    public void LanWorld_Display_IncludesAddressAndPort()
    {
        var world = new LanWorld("测试世界", 25565, "192.168.1.9", DateTime.Now);
        Assert.Contains("测试世界", world.Display);
        Assert.Contains("192.168.1.9:25565", world.Display);
    }

    [Fact]
    public void Scanner_StartStop_DoesNotThrowWhenPortBusy()
    {
        // 端口冲突时应抛 SocketException 让调用方回退到端口扫描,而不是崩掉
        using var scanner = new LanScanner();

        try
        {
            scanner.Start();
            Assert.True(scanner.IsRunning);
            scanner.Stop();
            Assert.False(scanner.IsRunning);
        }
        catch (System.Net.Sockets.SocketException)
        {
            // 本机 4445 已被占用(如游戏正在开局域网)属正常情况
        }
    }
}

/// <summary>
/// DownloadEngine 的 expectedSize 语义回归测试。
///
/// 起因:调用方传 0 想表达"大小未知",结果引擎按"已知空文件"处理,落了个 0 字节文件,
/// 直到服务端启动报 "Invalid or corrupt jarfile" 才暴露。
/// 这里把两种语义固定下来,避免再次踩坑。
/// </summary>
public class DownloadEngineSizeContractTests
{
    [Fact]
    public async Task DownloadFileAsync_ExpectedSizeZero_CreatesEmptyFileWithoutNetwork()
    {
        var dir = Path.Combine(Path.GetTempPath(), "daybreak-sizec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, "empty.bin");

        try
        {
            var engine = new DownloadEngine(DownloadMirror.Official);
            using var http = new HttpClient();

            // 0 = 已知空文件:不应发起网络请求,直接落空文件
            await engine.DownloadFileAsync(http, "https://example.invalid/should-not-be-requested",
                target, 0);

            Assert.True(File.Exists(target));
            Assert.Equal(0, new FileInfo(target).Length);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}