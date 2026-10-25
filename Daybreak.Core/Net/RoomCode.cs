using System.Security.Cryptography;
using System.Text;

namespace Daybreak.Core.Net;

/// <summary>一个联机房间的凭据(由 6 位房间号派生，双方输入同一房间号即得到同一组凭据)。</summary>
public record RoomCredentials(string RoomCode, string NetworkName, string NetworkSecret)
{
    /// <summary>房主固定使用的虚拟 IP(加入者由 EasyTier 自动分配)。</summary>
    public const string HostIp = "10.144.144.1";

    /// <summary>游戏里要连接的地址(房主视角下自己也是这个)。</summary>
    public string HostAddress => $"{HostIp}:25565";
}

/// <summary>
/// 6 位数字房间号。
///
/// 设计取舍(必须了解):
/// 房间号是**可猜的**(10 万~99 万,共 90 万种),任何人猜到房间号就能进入同一个房间。
/// 这是"好友手输 6 位数字"这个交互方式的固有代价,换来的是使用门槛极低。
/// 因此:
/// - 房间号里**不要放任何敏感信息**,屏幕分享/直播时注意遮挡
/// - 密钥由房间号单向派生,外部无法从网络流量反推房间号
/// - 如需更强安全性,后续可加"房间密码"作为第二因子
/// </summary>
public static class RoomCode
{
    public const int Length = 6;

    /// <summary>房间号范围:100000 ~ 999999(首位不为 0,保证始终 6 位)。</summary>
    private const int Min = 100_000;
    private const int Max = 999_999;

    /// <summary>派生根盐。改这个值会让所有旧房间号失效(不同版本间联机不兼容)。</summary>
    private const string DerivationSalt = "Daybreak-RoomCode-v1";

    /// <summary>网络名前缀,便于在 relay 上区分 Daybreak 的网络。</summary>
    private const string NetworkPrefix = "dbk";

    /// <summary>生成一个随机房间号。</summary>
    public static string Generate()
    {
        // RandomNumberGenerator 而非 Random:避免同一时刻多开生成相同序列
        var value = RandomNumberGenerator.GetInt32(Min, Max + 1);
        return value.ToString();
    }

    /// <summary>是否为合法的 6 位房间号。</summary>
    public static bool IsValid(string? code) =>
        !string.IsNullOrWhiteSpace(code) &&
        code.Length == Length &&
        code.All(char.IsAsciiDigit) &&
        code[0] != '0';

    /// <summary>规范化用户输入(去掉空格/常见分隔符),不合法时返回 null。</summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        // 用户可能粘贴 "123 456" 或 "123-456"
        var cleaned = new string(input.Where(char.IsAsciiDigit).ToArray());
        return IsValid(cleaned) ? cleaned : null;
    }

    /// <summary>
    /// 由房间号派生网络名与密钥。
    /// 同一房间号必须始终得到同一结果(否则双方连不到一起)，所以这里是纯函数、无随机数。
    ///
    /// 网络名与密钥都**不包含房间号明文** —— 中继节点上看不到房间号，
    /// 避免同在这个中继上的其他人反推出别人房间的号码。
    /// 两者用不同的派生标签，防止"名字是密钥前缀"这种低级泄漏。
    /// </summary>
    public static RoomCredentials Derive(string roomCode)
    {
        if (!IsValid(roomCode))
            throw new ArgumentException($"房间号必须是 {Length} 位数字且首位不为 0", nameof(roomCode));

        var nameDigest = DeriveDigest("name", roomCode);
        var secretDigest = DeriveDigest("secret", roomCode);

        // 网络名短一些，方便在 relay 日志里区分;密钥取长一些
        var networkName = $"{NetworkPrefix}-{nameDigest[..12]}";
        var networkSecret = secretDigest[..48];

        return new RoomCredentials(roomCode, networkName, networkSecret);
    }

    /// <summary>用 HMAC 做单向派生，不同标签得到互不相关的两段。</summary>
    private static string DeriveDigest(string label, string roomCode)
    {
        var key = Encoding.UTF8.GetBytes($"{DerivationSalt}:{label}");
        var data = Encoding.UTF8.GetBytes(roomCode);
        return Convert.ToHexStringLower(HMACSHA256.HashData(key, data));
    }
}