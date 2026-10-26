using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Daybreak.Core.Server;
using Xunit;

namespace Daybreak.Core.Tests;

/// <summary>
/// SLP(Server List Ping)客户端测试。
/// 用一个**桩服务端**回放协议,验证握手/VarInt/MOTD 拍平/人数解析是否正确——
/// 这些编码细节最容易写错,而错了只表现为"查询永远失败",很难排查。
/// </summary>
public class ServerListPingTests
{
    [Fact]
    public async Task QueryAsync_ParsesSimpleStatus()
    {
        var json = """
        {
          "version": { "name": "1.20.1", "protocol": 763 },
          "players": { "max": 20, "online": 7 },
          "description": { "text": "欢迎来到测试服" }
        }
        """;

        var port = await StartStubServerAsync(json);

        var status = await ServerListPing.QueryAsync("127.0.0.1", port, TimeSpan.FromSeconds(10));

        Assert.True(status.Online, status.Error);
        Assert.Equal(7, status.OnlinePlayers);
        Assert.Equal(20, status.MaxPlayers);
        Assert.Equal("1.20.1", status.VersionName);
        Assert.Equal(763, status.ProtocolVersion);
        Assert.Equal("欢迎来到测试服", status.Motd);
        Assert.Equal("7/20", status.PlayerDisplay);
    }

    [Fact]
    public async Task QueryAsync_FlattensRichTextMotd()
    {
        // 新版服务端的 MOTD 是富文本组件,text + extra 嵌套,必须拍平成一行
        var json = """
        {
          "version": { "name": "1.21", "protocol": 767 },
          "players": { "max": 100, "online": 0 },
          "description": {
            "text": "主服 ",
            "extra": [
              { "text": "| ", "color": "gray" },
              { "text": "欢迎", "color": "gold" }
            ]
          }
        }
        """;

        var port = await StartStubServerAsync(json);
        var status = await ServerListPing.QueryAsync("127.0.0.1", port, TimeSpan.FromSeconds(10));

        Assert.True(status.Online, status.Error);
        Assert.Equal("主服 | 欢迎", status.Motd);
        Assert.Equal("0/100", status.PlayerDisplay);
    }

    [Fact]
    public async Task QueryAsync_HandlesStringMotd()
    {
        // 老版本/部分服务端直接给字符串
        var json = """
        {
          "version": { "name": "1.8.9", "protocol": 47 },
          "players": { "max": 10, "online": 1 },
          "description": "简单的字符串 MOTD"
        }
        """;

        var port = await StartStubServerAsync(json);
        var status = await ServerListPing.QueryAsync("127.0.0.1", port, TimeSpan.FromSeconds(10));

        Assert.True(status.Online, status.Error);
        Assert.Equal("简单的字符串 MOTD", status.Motd);
    }

    [Fact]
    public async Task QueryAsync_ReportsRefusedConnectionAsHumanReadable()
    {
        // 找一个没人监听的端口
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var status = await ServerListPing.QueryAsync("127.0.0.1", port, TimeSpan.FromSeconds(5));

        Assert.False(status.Online);
        Assert.NotNull(status.Error);
        // 错误信息要能让人看懂,而不是抛原始 SocketException
        Assert.Contains("拒绝连接", status.Error);
        Assert.Equal("—", status.PlayerDisplay);
    }

    [Fact]
    public void Offline_HelperBuildsConsistentResult()
    {
        var status = ServerStatus.Offline("测试错误");

        Assert.False(status.Online);
        Assert.Equal("测试错误", status.Error);
        Assert.Equal("—", status.PlayerDisplay);
    }

    /// <summary>
    /// 桩服务端:按 SLP 协议读握手与状态请求,回一个状态响应。
    /// 同时验证客户端发来的包结构合法(VarInt 长度前缀 / 包 id / 字符串编码)。
    /// </summary>
    private static async Task<int> StartStubServerAsync(string statusJson)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        _ = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync();
                await using var stream = client.GetStream();

                // 读握手包
                var handshake = await ReadPacketAsync(stream);
                Assert.Equal(0x00, handshake[0]);   // 包 id
                // 协议版本我们固定发 -1(仅查询),这里只确认包不是空的
                Assert.True(handshake.Length > 3);

                // 读状态请求(空负载)
                var request = await ReadPacketAsync(stream);
                Assert.Empty(request);

                // 回状态响应
                await WritePacketAsync(stream, BuildStatusResponse(statusJson));

                // 读 ping,回 pong
                try
                {
                    var ping = await ReadPacketAsync(stream);
                    await WritePacketAsync(stream, ping);
                }
                catch (Exception)
                {
                    // 客户端可能不测延迟,忽略
                }

                listener.Stop();
            }
            catch (Exception)
            {
                // 桩服务端出错不影响断言(客户端会报连接/解析失败)
            }
        });

        await Task.Delay(50);
        return port;
    }

    private static byte[] BuildStatusResponse(string json)
    {
        using var ms = new MemoryStream();
        WriteVarInt(ms, 0x00);                                  // 包 id
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        WriteVarInt(ms, jsonBytes.Length);                      // 字符串长度前缀
        ms.Write(jsonBytes);
        return ms.ToArray();
    }

    private static async Task<byte[]> ReadPacketAsync(Stream stream)
    {
        var length = await ReadVarIntAsync(stream);
        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, length - read));
            if (n == 0)
                break;
            read += n;
        }
        return buffer;
    }

    private static async Task WritePacketAsync(Stream stream, byte[] payload)
    {
        using var ms = new MemoryStream();
        WriteVarInt(ms, payload.Length);
        ms.Write(payload);
        await stream.WriteAsync(ms.ToArray());
        await stream.FlushAsync();
    }

    private static void WriteVarInt(Stream stream, int value)
    {
        var unsigned = (uint)value;
        do
        {
            var temp = (byte)(unsigned & 0x7F);
            unsigned >>= 7;
            if (unsigned != 0)
                temp |= 0x80;
            stream.WriteByte(temp);
        }
        while (unsigned != 0);
    }

    private static async Task<int> ReadVarIntAsync(Stream stream)
    {
        var result = 0;
        var shift = 0;
        var buffer = new byte[1];

        while (true)
        {
            var n = await stream.ReadAsync(buffer);
            if (n == 0)
                throw new EndOfStreamException();

            var b = buffer[0];
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return result;

            shift += 7;
            if (shift > 35)
                throw new InvalidDataException("VarInt 过长");
        }
    }
}