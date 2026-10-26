using System.IO;
using Daybreak.Core.Server;
using Xunit;

namespace Daybreak.Core.Tests;

/// <summary>
/// 服务器列表业务逻辑。地址解析是重点:用户会从各处复制地址,
/// 格式五花八门(带 http:// 前缀、带路径、带端口、IPv6),
/// 解析错了表现为"明明地址没错却连不上"。
/// </summary>
public class ServerListServiceTests
{
    private static string NewGameDir() =>
        Path.Combine(Path.GetTempPath(), "daybreak-sl-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("mc.example.com", "mc.example.com", 25565)]
    [InlineData("mc.example.com:25566", "mc.example.com", 25566)]
    [InlineData("  mc.example.com  ", "mc.example.com", 25565)]
    [InlineData("https://mc.example.com", "mc.example.com", 25565)]
    [InlineData("http://mc.example.com:25570", "mc.example.com", 25570)]
    [InlineData("mc.example.com/", "mc.example.com", 25565)]
    [InlineData("1.2.3.4:25565", "1.2.3.4", 25565)]
    public void ParseAddress_HandlesCommonInputFormats(string input, string host, int port)
    {
        var (parsedHost, parsedPort) = ServerListService.ParseAddress(input);

        Assert.Equal(host, parsedHost);
        Assert.Equal(port, parsedPort);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ParseAddress_EmptyInput_ReturnsEmptyHost(string? input)
    {
        var (host, port) = ServerListService.ParseAddress(input);
        Assert.Equal("", host);
        Assert.Equal(25565, port);
    }

    [Fact]
    public void ParseAddress_PortLikeSuffixThatIsNotValidPort_KeepsWholeAsHost()
    {
        // "host:99999" 端口越界,不应被当成端口——否则会得到一个错误的地址
        var (host, port) = ServerListService.ParseAddress("mc.example.com:99999");

        Assert.Equal("mc.example.com:99999", host);
        Assert.Equal(25565, port);
    }

    [Fact]
    public void Add_ThenList_RoundTrips()
    {
        var dir = NewGameDir();
        try
        {
            var service = new ServerListService(dir);
            service.Add("mc.example.com:25566", name: "朋友的服", note: "晚上开");

            var list = service.List();
            Assert.Single(list);
            Assert.Equal("朋友的服", list[0].DisplayName);
            Assert.Equal("mc.example.com:25566", list[0].Address);
            Assert.Equal("晚上开", list[0].Note);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Add_DuplicateAddress_Throws()
    {
        var dir = NewGameDir();
        try
        {
            var service = new ServerListService(dir);
            service.Add("mc.example.com");

            // 同一个地址(含大小写差异)不应重复添加
            var ex = Assert.Throws<InvalidOperationException>(() => service.Add("MC.EXAMPLE.COM"));
            Assert.Contains("已在列表中", ex.Message);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Add_EmptyAddress_Throws()
    {
        var dir = NewGameDir();
        try
        {
            var service = new ServerListService(dir);
            Assert.Throws<InvalidOperationException>(() => service.Add("   "));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Remove_DeletesOnlyMatchingEntry()
    {
        var dir = NewGameDir();
        try
        {
            var service = new ServerListService(dir);
            service.Add("a.example.com");
            service.Add("b.example.com");

            service.Remove("a.example.com", 25565);

            var list = service.List();
            Assert.Single(list);
            Assert.Equal("b.example.com", list[0].Host);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Update_PersistsFavoriteFlag()
    {
        var dir = NewGameDir();
        try
        {
            var service = new ServerListService(dir);
            var server = service.Add("mc.example.com");

            server.Favorite = true;
            server.Name = "改名后的服";
            service.Update(server);

            var reloaded = service.List()[0];
            Assert.True(reloaded.Favorite);
            Assert.Equal("改名后的服", reloaded.Name);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DisplayName_FallsBackToHostWhenNameMissing()
    {
        var server = new SavedServer { Host = "mc.example.com", Port = 25565 };
        Assert.Equal("mc.example.com", server.DisplayName);
    }

    [Fact]
    public void Address_OmitsDefaultPort()
    {
        var defaultPort = new SavedServer { Host = "mc.example.com", Port = 25565 };
        var customPort = new SavedServer { Host = "mc.example.com", Port = 25566 };

        Assert.Equal("mc.example.com", defaultPort.Address);
        Assert.Equal("mc.example.com:25566", customPort.Address);
    }

    [Fact]
    public void BuildDirectConnectArguments_UsesOriginalHostNotSrvTarget()
    {
        // 直连参数要传用户填的原始域名:游戏客户端自己会做 SRV 解析,
        // 如果我们提前换成 SRV 目标,服务端反代会拿不到正确的虚拟主机
        var server = new SavedServer { Host = "2b2t.org", Port = 25565 };

        var args = ServerListService.BuildDirectConnectArguments(server);

        Assert.Equal(["--server", "2b2t.org", "--port", "25565"], args);
    }

    [Fact]
    public void ServerListStore_CorruptFile_ReturnsEmptyInsteadOfThrowing()
    {
        var dir = NewGameDir();
        Directory.CreateDirectory(dir);

        try
        {
            File.WriteAllText(ServerListStore.StorePath(dir), "not json at all");
            Assert.Empty(ServerListStore.Load(dir).Servers);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RefreshAllAsync_UnreachableServer_MarksOfflineWithoutThrowing()
    {
        var dir = NewGameDir();
        try
        {
            var service = new ServerListService(dir);

            // 一个必定连不上的地址(保留给文档示例的域名,不会解析成功)
            var servers = new List<SavedServer>
            {
                new() { Host = "daybreak-unreachable.invalid", Port = 25565 },
            };

            var reported = new List<SavedServer>();
            await service.RefreshAllAsync(servers, s => reported.Add(s),
                TimeSpan.FromSeconds(5));

            Assert.Single(reported);
            Assert.False(reported[0].IsOnline);
            Assert.NotNull(reported[0].Status?.Error);
            Assert.Equal("—", reported[0].PlayerDisplay);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}