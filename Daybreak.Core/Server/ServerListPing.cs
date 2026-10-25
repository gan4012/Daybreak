using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Daybreak.Core.Server;

/// <summary>服务器状态查询结果。</summary>
public record ServerStatus(
    bool Online,
    string? Motd,
    int OnlinePlayers,
    int MaxPlayers,
    string? VersionName,
    int ProtocolVersion,
    long LatencyMs,
    string? Favicon,
    string? Error)
{
    public static ServerStatus Offline(string error) =>
        new(false, null, 0, 0, null, 0, 0, null, error);

    /// <summary>"12/20" 形式的人数显示。</summary>
    public string PlayerDisplay => Online ? $"{OnlinePlayers}/{MaxPlayers}" : "—";
}

/// <summary>
/// Minecraft 服务器列表查询(Server List Ping,SLP)。
///
/// 协议流程(全部是长度前缀的包):
///   1. 连接 TCP
///   2. 发 Handshake(0x00):协议版本 / 地址 / 端口 / 下一状态=1(状态查询)
///   3. 发 Status Request(0x00,空包)
///   4. 读 Status Response(0x00):一个 JSON 字符串
///   5. 发 Ping(0x01)+ 读 Pong,用往返时间算延迟
///
/// 这是**纯客户端协议**,不依赖任何服务端组件,也不涉及任何非公开接口。
/// </summary>
public static class ServerListPing
{
    /// <summary>我们声明支持的协议版本。填 -1 表示"仅查询,不实际进服",服务端会照常返回状态。</summary>
    private const int HandshakeProtocolVersion = -1;

    public static async Task<ServerStatus> QueryAsync(
        string host, int port, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(5);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(limit);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // 先查 SRV:很多服务器把真实入口放在 SRV 记录里,
        // 直连域名的 A 记录会打到前置 CDN 上,表现为超时或流被提前关闭。
        var connectHost = host;
        var connectPort = port;
        try
        {
            var srv = await SrvResolver.ResolveMinecraftAsync(host, TimeSpan.FromSeconds(4), cts.Token);
            if (srv is not null)
            {
                connectHost = srv.Target;
                connectPort = srv.Port;
            }
        }
        catch (Exception)
        {
            // SRV 查不到就按原地址连,不影响主流程
        }

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(connectHost, connectPort, cts.Token);

            await using var stream = client.GetStream();

            // 注意:握手里要填**原始域名**而不是 SRV 指向的目标主机,
            // 否则服务端(尤其是反代/多服共用一个入口时)无法判断玩家想进哪个服。
            await WritePacketAsync(stream, BuildHandshake(host, port), cts.Token);

            // 2. Status Request(空负载)
            await WritePacketAsync(stream, [], cts.Token);

            // 3. Status Response
            var payload = await ReadPacketAsync(stream, cts.Token);
            var status = ParseStatusPayload(payload);

            stopwatch.Stop();

            // 4. Ping/Pong 测延迟(失败不影响状态结果)
            long latency = stopwatch.ElapsedMilliseconds;
            try
            {
                latency = await MeasurePingAsync(stream, cts.Token);
            }
            catch (Exception)
            {
                // 有些服务端/代理不响应 ping,用握手往返时间兜底
            }

            return status with { LatencyMs = latency };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ServerStatus.Offline($"连接超时(超过 {limit.TotalSeconds:0} 秒)");
        }
        catch (SocketException ex)
        {
            return ServerStatus.Offline(ex.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => "拒绝连接(服务端没开或端口不对)",
                SocketError.HostNotFound => "找不到该地址(域名拼写错误?)",
                SocketError.TimedOut => "连接超时(可能被防火墙拦截,或该服务器国内不可达)",
                SocketError.NetworkUnreachable => "网络不可达",
                _ => $"连接失败:{ex.SocketErrorCode}",
            });
        }
        catch (Exception ex)
        {
            return ServerStatus.Offline($"查询失败:{ex.Message}");
        }
    }

    // ===== 包构造 =====

    private static byte[] BuildHandshake(string host, int port)
    {
        using var ms = new MemoryStream();
        WriteVarInt(ms, 0x00);                          // packet id
        WriteVarInt(ms, HandshakeProtocolVersion);      // protocol version
        WriteString(ms, host);                          // server address
        ms.WriteByte((byte)(port >> 8));                // port (unsigned short, big-endian)
        ms.WriteByte((byte)(port & 0xFF));
        WriteVarInt(ms, 1);                             // next state = 1 (status)
        return ms.ToArray();
    }

    private static async Task WritePacketAsync(Stream stream, byte[] payload, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        WriteVarInt(ms, payload.Length);
        ms.Write(payload);

        await stream.WriteAsync(ms.ToArray(), ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<byte[]> ReadPacketAsync(Stream stream, CancellationToken ct)
    {
        var length = await ReadVarIntAsync(stream, ct);
        if (length <= 0 || length > 4 * 1024 * 1024)
            throw new InvalidDataException($"包长度异常:{length}");

        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, length - read), ct);
            if (n == 0)
                throw new EndOfStreamException("服务端提前关闭了连接");
            read += n;
        }

        return buffer;
    }

    /// <summary>解析 Status Response:包 id(0x00)+ JSON 字符串。</summary>
    private static ServerStatus ParseStatusPayload(byte[] payload)
    {
        using var ms = new MemoryStream(payload);
        var packetId = ReadVarInt(ms);
        if (packetId != 0x00)
            throw new InvalidDataException($"期望状态响应包(0x00),收到 0x{packetId:X2}");

        var json = ReadString(ms);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var online = 0;
        var max = 0;
        if (root.TryGetProperty("players", out var players))
        {
            if (players.TryGetProperty("online", out var o) && o.ValueKind == JsonValueKind.Number)
                online = o.GetInt32();
            if (players.TryGetProperty("max", out var m) && m.ValueKind == JsonValueKind.Number)
                max = m.GetInt32();
        }

        string? versionName = null;
        var protocol = 0;
        if (root.TryGetProperty("version", out var version))
        {
            versionName = version.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (version.TryGetProperty("protocol", out var p) && p.ValueKind == JsonValueKind.Number)
                protocol = p.GetInt32();
        }

        var motd = root.TryGetProperty("description", out var desc) ? FlattenMotd(desc) : null;
        var favicon = root.TryGetProperty("favicon", out var fav) ? fav.GetString() : null;

        return new ServerStatus(true, motd, online, max, versionName, protocol, 0, favicon, null);
    }

    /// <summary>
    /// MOTD 在协议里可能是字符串,也可能是富文本组件对象。
    /// 新版服务端普遍用 {"text":"...","extra":[...]} 结构,需要拍平成一行纯文本。
    /// </summary>
    private static string FlattenMotd(JsonElement description)
    {
        var sb = new StringBuilder();
        Flatten(description, sb);
        return sb.ToString().Trim();
    }

    private static void Flatten(JsonElement element, StringBuilder sb)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                sb.Append(element.GetString());
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    Flatten(item, sb);
                break;

            case JsonValueKind.Object:
                if (element.TryGetProperty("text", out var text))
                    Flatten(text, sb);

                if (element.TryGetProperty("extra", out var extra))
                    Flatten(extra, sb);
                break;
        }
    }

    // ===== 延迟测量 =====

    private static async Task<long> MeasurePingAsync(Stream stream, CancellationToken ct)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        using var ms = new MemoryStream();
        WriteVarInt(ms, 0x01);                                  // packet id = 1 (ping)
        Span<byte> payload = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(payload, timestamp);
        ms.Write(payload);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await WritePacketAsync(stream, ms.ToArray(), ct);
        await ReadPacketAsync(stream, ct);
        stopwatch.Stop();

        return stopwatch.ElapsedMilliseconds;
    }

    // ===== VarInt / String 编解码 =====

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

    private static int ReadVarInt(Stream stream)
    {
        var result = 0;
        var shift = 0;

        while (true)
        {
            var b = stream.ReadByte();
            if (b < 0)
                throw new EndOfStreamException("读取 VarInt 时流已结束");

            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return result;

            shift += 7;
            if (shift > 35)
                throw new InvalidDataException("VarInt 过长");
        }
    }

    private static async Task<int> ReadVarIntAsync(Stream stream, CancellationToken ct)
    {
        var result = 0;
        var shift = 0;
        var buffer = new byte[1];

        while (true)
        {
            var n = await stream.ReadAsync(buffer, ct);
            if (n == 0)
                throw new EndOfStreamException("读取 VarInt 时流已结束");

            var b = buffer[0];
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return result;

            shift += 7;
            if (shift > 35)
                throw new InvalidDataException("VarInt 过长");
        }
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarInt(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static string ReadString(Stream stream)
    {
        var length = ReadVarInt(stream);
        if (length < 0 || length > 1024 * 1024)
            throw new InvalidDataException($"字符串长度异常:{length}");

        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = stream.Read(buffer, read, length - read);
            if (n == 0)
                throw new EndOfStreamException("读取字符串时流已结束");
            read += n;
        }

        return Encoding.UTF8.GetString(buffer);
    }
}