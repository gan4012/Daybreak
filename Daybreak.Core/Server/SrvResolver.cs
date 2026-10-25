using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Daybreak.Core.Server;

/// <summary>SRV 记录解析结果。</summary>
public record SrvRecord(string Target, int Port, int Priority, int Weight);

/// <summary>
/// Minecraft 服务器的 SRV 记录解析。
///
/// **为什么必须做**:很多服务器(例如 2b2t.org)把真实入口放在 SRV 记录里
/// (`_minecraft._tcp.2b2t.org` → `connect.2b2t.org:25565`)。
/// 直接连域名的 A 记录会打到前置 CDN/负载均衡上,对方不说 Minecraft 协议,
/// 表现为"连接超时"或"流被提前关闭"——用户只会看到查询失败,完全不知道为什么。
///
/// **为什么手写 DNS**:.NET 的 System.Net.Dns **不提供 SRV 查询**
/// (只有 A/AAAA/MX 等),所以这里直接构造 DNS 报文走 UDP 53 端口。
/// </summary>
public static class SrvResolver
{
    private const int TypeSrv = 33;
    private const int ClassIn = 1;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// 查询 _minecraft._tcp.&lt;host&gt; 的 SRV 记录。
    /// 没有记录 / 查询失败时返回 null(调用方应回退到直接用原主机名与端口)。
    /// </summary>
    public static async Task<SrvRecord?> ResolveMinecraftAsync(
        string host, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        // 已经是 IP 就不用查 SRV
        if (IPAddress.TryParse(host, out _))
            return null;

        var query = "_minecraft._tcp." + host.TrimEnd('.');

        try
        {
            var records = await QueryAsync(query, timeout ?? DefaultTimeout, ct);
            if (records.Count == 0)
                return null;

            // RFC 2782:先按 priority 升序,再按 weight 降序
            return records
                .OrderBy(r => r.Priority)
                .ThenByDescending(r => r.Weight)
                .First();
        }
        catch (Exception)
        {
            // SRV 查询失败不算致命错误,回退到直连域名
            return null;
        }
    }

    /// <summary>执行一次 SRV 查询并解析应答。</summary>
    public static async Task<List<SrvRecord>> QueryAsync(
        string name, TimeSpan timeout, CancellationToken ct = default)
    {
        var results = new List<SrvRecord>();

        // 取系统配置的 DNS 服务器(不硬编码 8.8.8.8:国内网络访问公共 DNS 常被拦)
        var servers = GetDnsServers();
        if (servers.Count == 0)
            return results;

        var queryId = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        var packet = BuildQuery(name, queryId);

        foreach (var server in servers)
        {
            try
            {
                using var udp = new UdpClient();
                udp.Client.ReceiveTimeout = (int)timeout.TotalMilliseconds;
                await udp.SendAsync(packet, packet.Length, new IPEndPoint(server, 53));

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);

                var response = await udp.ReceiveAsync(cts.Token);
                results = ParseResponse(response.Buffer, queryId);
                if (results.Count > 0)
                    return results;
            }
            catch (Exception)
            {
                // 换下一个 DNS 服务器重试
            }
        }

        return results;
    }

    /// <summary>读取系统 DNS 服务器列表。</summary>
    private static List<IPAddress> GetDnsServers()
    {
        var servers = new List<IPAddress>();

        try
        {
            var adapters = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
            foreach (var adapter in adapters)
            {
                if (adapter.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                    continue;

                foreach (var address in adapter.GetIPProperties().DnsAddresses)
                {
                    if (!servers.Contains(address))
                        servers.Add(address);
                }
            }
        }
        catch (Exception)
        {
            // 拿不到网卡信息时用下面的兜底
        }

        // 兜底:常见的公共 DNS
        if (servers.Count == 0)
        {
            servers.Add(IPAddress.Parse("223.5.5.5"));   // 阿里 DNS,国内可达性好
            servers.Add(IPAddress.Parse("1.1.1.1"));
        }

        return servers;
    }

    // ===== DNS 报文构造与解析 =====

    private static byte[] BuildQuery(string name, ushort id)
    {
        using var ms = new MemoryStream();

        // Header: ID / Flags(标准查询 + 期望递归) / QDCOUNT=1 / ANCOUNT=0 / NSCOUNT=0 / ARCOUNT=0
        WriteUInt16(ms, id);
        WriteUInt16(ms, 0x0100);
        WriteUInt16(ms, 1);
        WriteUInt16(ms, 0);
        WriteUInt16(ms, 0);
        WriteUInt16(ms, 0);

        // Question: QNAME(标签序列) + QTYPE + QCLASS
        foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length > 63)
                throw new InvalidOperationException("DNS 标签过长");

            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes);
        }
        ms.WriteByte(0);                    // 名称结束

        WriteUInt16(ms, TypeSrv);
        WriteUInt16(ms, ClassIn);

        return ms.ToArray();
    }

    private static void WriteUInt16(Stream stream, int value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)(value & 0xFF));
    }

    private static List<SrvRecord> ParseResponse(byte[] buffer, ushort expectedId)
    {
        var records = new List<SrvRecord>();

        if (buffer.Length < 12)
            return records;

        var id = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(0, 2));
        if (id != expectedId)
            return records;

        // RCODE 低 4 位:0=成功,3=NXDOMAIN(域名不存在,属正常情况)
        var flags = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(2, 2));
        var rcode = flags & 0x000F;
        if (rcode != 0)
            return records;

        var questionCount = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(4, 2));
        var answerCount = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(6, 2));

        var offset = 12;

        // 跳过 Question 区
        for (var i = 0; i < questionCount; i++)
        {
            offset = SkipName(buffer, offset);
            offset += 4;    // QTYPE + QCLASS
            if (offset > buffer.Length)
                return records;
        }

        // 解析 Answer 区
        for (var i = 0; i < answerCount; i++)
        {
            if (offset >= buffer.Length)
                break;

            offset = SkipName(buffer, offset);
            if (offset + 10 > buffer.Length)
                break;

            var type = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset, 2));
            var dataLength = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset + 8, 2));
            offset += 10;

            if (offset + dataLength > buffer.Length)
                break;

            if (type == TypeSrv && dataLength >= 7)
            {
                // SRV RDATA: priority(2) weight(2) port(2) target(name)
                var priority = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset, 2));
                var weight = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset + 2, 2));
                var port = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset + 4, 2));
                var target = ReadName(buffer, offset + 6);

                if (!string.IsNullOrEmpty(target))
                    records.Add(new SrvRecord(target, port, priority, weight));
            }

            offset += dataLength;
        }

        return records;
    }

    /// <summary>跳过名称字段(支持 DNS 压缩指针)。</summary>
    private static int SkipName(byte[] buffer, int offset)
    {
        while (offset < buffer.Length)
        {
            var length = buffer[offset];

            if (length == 0)
                return offset + 1;

            // 高两位是 11 表示压缩指针(2 字节)
            if ((length & 0xC0) == 0xC0)
                return offset + 2;

            offset += 1 + length;
        }

        return offset;
    }

    /// <summary>读取名称字段(处理压缩指针)。</summary>
    private static string ReadName(byte[] buffer, int offset, int depth = 0)
    {
        if (depth > 10)
            return "";      // 防指针环

        var labels = new List<string>();

        while (offset < buffer.Length)
        {
            var length = buffer[offset];

            if (length == 0)
                break;

            if ((length & 0xC0) == 0xC0)
            {
                // 压缩指针:低 14 位是实际偏移
                if (offset + 1 >= buffer.Length)
                    break;

                var pointer = ((length & 0x3F) << 8) | buffer[offset + 1];
                var rest = ReadName(buffer, pointer, depth + 1);
                if (!string.IsNullOrEmpty(rest))
                    labels.Add(rest);
                break;
            }

            offset++;
            if (offset + length > buffer.Length)
                break;

            labels.Add(Encoding.ASCII.GetString(buffer, offset, length));
            offset += length;
        }

        return string.Join('.', labels);
    }
}