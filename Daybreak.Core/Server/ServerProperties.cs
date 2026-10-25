using System.IO;
using System.Text;

namespace Daybreak.Core.Server;

/// <summary>
/// server.properties 的读写。
///
/// 设计要点:**保留注释、空行与未知键的原始顺序**。
/// 原版生成的 server.properties 带大量注释说明,直接反序列化成字典再写回会把注释全丢掉,
/// 用户后续手动改配置时就失去了参考;所以这里按"行"建模,只替换目标键的值。
/// </summary>
public class ServerProperties
{
    private readonly List<string> _lines = [];

    /// <summary>按键取值的后备存储(用于记录本文件中出现的键顺序)。</summary>
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public static ServerProperties Load(string path)
    {
        var props = new ServerProperties();
        if (File.Exists(path))
            props.Parse(File.ReadAllLines(path, Encoding.UTF8));
        return props;
    }

    private void Parse(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            _lines.Add(line);

            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith('!'))
                continue;

            var sep = line.IndexOf('=');
            if (sep <= 0)
                continue;

            var key = line[..sep].Trim();
            var value = line[(sep + 1)..];
            _values[key] = value;
        }
    }

    /// <summary>读取字符串值;不存在时返回 fallback。</summary>
    public string Get(string key, string fallback = "")
        => _values.TryGetValue(key, out var value) ? value : fallback;

    public int GetInt(string key, int fallback)
        => int.TryParse(Get(key), out var value) ? value : fallback;

    public bool GetBool(string key, bool fallback)
        => Get(key).Equals("true", StringComparison.OrdinalIgnoreCase) ? true
         : Get(key).Equals("false", StringComparison.OrdinalIgnoreCase) ? false
         : fallback;

    /// <summary>设置值:已有键就替换该行,没有就追加到末尾。</summary>
    public void Set(string key, string value)
    {
        for (var i = 0; i < _lines.Count; i++)
        {
            var line = _lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith('!'))
                continue;

            var sep = line.IndexOf('=');
            if (sep <= 0)
                continue;

            if (line[..sep].Trim() == key)
            {
                _lines[i] = $"{key}={value}";
                _values[key] = value;
                return;
            }
        }

        _lines.Add($"{key}={value}");
        _values[key] = value;
    }

    public void Set(string key, int value) => Set(key, value.ToString());
    public void Set(string key, bool value) => Set(key, value ? "true" : "false");

    /// <summary>文件里是否已有该键。</summary>
    public bool Contains(string key) => _values.ContainsKey(key);

    /// <summary>所有出现过的键(按文件中顺序)。</summary>
    public IEnumerable<string> Keys => _values.Keys;

    public string ToText() => string.Join(Environment.NewLine, _lines) + Environment.NewLine;

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // 原版服务端读写用 UTF-8 无 BOM;带 BOM 时 Java 的 Properties.load 会把 BOM 当成键的一部分
        File.WriteAllText(path, ToText(), new UTF8Encoding(false));
    }

    /// <summary>
    /// 生成一份带常用注释的默认配置(首次开服时用,比原版自动生成的更易读)。
    /// 原版会在首次启动时自己补全所有缺失键,所以这里只写关键项即可。
    /// </summary>
    public static ServerProperties CreateDefault(int port, int maxPlayers = 20, string motd = "Daybreak 服务器")
    {
        var props = new ServerProperties();

        props._lines.Add("#Minecraft server properties");
        props._lines.Add("#由 Daybreak 破晓启动器生成;本文件可在启动器「联机」页可视化编辑");
        props._lines.Add($"motd={motd}");
        props._lines.Add($"server-port={port}");
        props._lines.Add($"max-players={maxPlayers}");
        props._lines.Add("#正版验证:true 时只允许正版账号进入(联机请保持 true)");
        props._lines.Add("online-mode=true");
        props._lines.Add("#白名单:开启后只有 whitelist.json 中的玩家能进");
        props._lines.Add("white-list=false");
        props._lines.Add("enforce-whitelist=false");
        props._lines.Add("difficulty=easy");
        props._lines.Add("gamemode=survival");
        props._lines.Add("pvp=true");
        props._lines.Add("view-distance=10");
        props._lines.Add("simulation-distance=10");
        props._lines.Add("spawn-protection=16");
        props._lines.Add("allow-flight=false");
        props._lines.Add("allow-nether=true");
        props._lines.Add("enable-command-block=false");
        props._lines.Add("level-name=world");

        foreach (var line in props._lines)
        {
            var sep = line.IndexOf('=');
            if (sep > 0)
                props._values[line[..sep].Trim()] = line[(sep + 1)..];
        }

        return props;
    }
}