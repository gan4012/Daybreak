using System.IO;
using Daybreak.Core.Server;
using Xunit;

namespace Daybreak.Core.Tests;

/// <summary>server.properties 读写:重点是**保留注释与未知键**。</summary>
public class ServerPropertiesTests
{
    [Fact]
    public void Load_ThenSet_KeepsCommentsAndUnknownKeys()
    {
        var path = Path.Combine(Path.GetTempPath(), "daybreak-props-" + Guid.NewGuid().ToString("N") + ".properties");
        var original = string.Join(Environment.NewLine,
            "#Minecraft server properties",
            "#这是一行注释",
            "motd=原始标题",
            "server-port=25565",
            "my-custom-key=第三方保留值",
            "",
            "difficulty=easy") + Environment.NewLine;

        File.WriteAllText(path, original);

        try
        {
            var props = ServerProperties.Load(path);
            props.Set("motd", "新标题");
            props.Save(path);

            var text = File.ReadAllText(path);

            // 注释必须还在
            Assert.Contains("#Minecraft server properties", text);
            Assert.Contains("#这是一行注释", text);
            // 未知键必须还在(不能因为不认识就丢掉)
            Assert.Contains("my-custom-key=第三方保留值", text);
            // 空行结构保留
            Assert.Contains("difficulty=easy", text);
            // 目标键被替换
            Assert.Contains("motd=新标题", text);
            Assert.DoesNotContain("motd=原始标题", text);
            // 没改的键不动
            Assert.Contains("server-port=25565", text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Set_NewKey_AppendsToEnd()
    {
        var path = Path.Combine(Path.GetTempPath(), "daybreak-props-" + Guid.NewGuid().ToString("N") + ".properties");
        File.WriteAllText(path, "motd=a" + Environment.NewLine);

        try
        {
            var props = ServerProperties.Load(path);
            props.Set("white-list", true);
            props.Save(path);

            var lines = File.ReadAllLines(path).Where(l => l.Length > 0).ToArray();
            Assert.Equal("motd=a", lines[0]);
            Assert.Equal("white-list=true", lines[^1]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GetBool_HandlesTrueFalseAndFallback()
    {
        var path = Path.Combine(Path.GetTempPath(), "daybreak-props-" + Guid.NewGuid().ToString("N") + ".properties");
        File.WriteAllText(path, string.Join(Environment.NewLine,
            "online-mode=true",
            "pvp=false",
            "allow-flight=notabool"));

        try
        {
            var props = ServerProperties.Load(path);
            Assert.True(props.GetBool("online-mode", false));
            Assert.False(props.GetBool("pvp", true));
            // 无法解析时回退到默认值,而不是当成 false
            Assert.True(props.GetBool("allow-flight", true));
            // 键不存在同样回退
            Assert.False(props.GetBool("missing-key", false));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GetInt_ParsesAndFallsBack()
    {
        var path = Path.Combine(Path.GetTempPath(), "daybreak-props-" + Guid.NewGuid().ToString("N") + ".properties");
        File.WriteAllText(path, "server-port=25566" + Environment.NewLine);

        try
        {
            var props = ServerProperties.Load(path);
            Assert.Equal(25566, props.GetInt("server-port", 25565));
            Assert.Equal(25565, props.GetInt("view-distance", 25565));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CreateDefault_HasExpectedSafetyDefaults()
    {
        var props = ServerProperties.CreateDefault(25570, maxPlayers: 30, motd: "测试服");

        // 正版验证默认开启 —— 这是安全默认值,不能被改成 false
        Assert.True(props.GetBool("online-mode", false));
        Assert.Equal(25570, props.GetInt("server-port", 0));
        Assert.Equal(30, props.GetInt("max-players", 0));
        Assert.Equal("测试服", props.Get("motd"));
        Assert.False(props.GetBool("white-list", true));
        Assert.True(props.Contains("level-name"));
    }

    [Fact]
    public void Save_WritesUtf8WithoutBom()
    {
        var path = Path.Combine(Path.GetTempPath(), "daybreak-props-" + Guid.NewGuid().ToString("N") + ".properties");

        try
        {
            ServerProperties.CreateDefault(25565, motd: "中文标题").Save(path);

            // 带 BOM 时 Java 的 Properties.load 会把 BOM 当成第一个键的一部分,导致 motd 读不出来
            var bytes = File.ReadAllBytes(path);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "server.properties 不能带 UTF-8 BOM");
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>EULA 处理:合规要求"必须用户显式同意"。</summary>
public class EulaServiceTests
{
    [Fact]
    public void IsAccepted_MissingFile_IsFalse()
    {
        var dir = Path.Combine(Path.GetTempPath(), "daybreak-eula-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            // 文件不存在 = 未同意(不能默认放行)
            Assert.False(EulaService.IsAccepted(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Accept_ThenIsAccepted_IsTrue()
    {
        var dir = Path.Combine(Path.GetTempPath(), "daybreak-eula-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            EulaService.Accept(dir);
            Assert.True(EulaService.IsAccepted(dir));

            var text = File.ReadAllText(EulaService.EulaPath(dir));
            Assert.Contains("eula=true", text);
            // 必须保留 EULA 链接,便于用户回看条款
            Assert.Contains("aka.ms/MinecraftEULA", text);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Revoke_FlipsToFalseWithoutDeletingFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "daybreak-eula-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            EulaService.Accept(dir);
            EulaService.Revoke(dir);

            Assert.False(EulaService.IsAccepted(dir));
            // 写回 false 而不是删文件,让用户能看出"曾经同意过又撤回"
            Assert.True(File.Exists(EulaService.EulaPath(dir)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void IsAccepted_IgnoresCommentedLines()
    {
        var dir = Path.Combine(Path.GetTempPath(), "daybreak-eula-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            // 原版生成的 eula.txt 里注释就含 "eula" 字样,不能被误判
            File.WriteAllText(EulaService.EulaPath(dir),
                "#By changing the setting below to TRUE you are indicating your agreement to our EULA" +
                Environment.NewLine + "eula=false" + Environment.NewLine);

            Assert.False(EulaService.IsAccepted(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

/// <summary>实例列表持久化与展示字段。</summary>
public class ServerStoreTests
{
    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "daybreak-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            var store = new ServerStore();
            store.Servers.Add(new ServerInstance
            {
                Name = "生存服",
                Type = ServerType.Fabric,
                GameVersion = "1.20.1",
                LoaderVersion = "0.15.11",
                Port = 25570,
                MaxMemoryMb = 4096,
                AcceptEula = true,
            });
            store.Save(dir);

            var loaded = ServerStore.Load(dir);
            Assert.Single(loaded.Servers);

            var s = loaded.Servers[0];
            Assert.Equal("生存服", s.Name);
            Assert.Equal(ServerType.Fabric, s.Type);
            Assert.Equal(25570, s.Port);
            Assert.Equal(4096, s.MaxMemoryMb);
            Assert.True(s.AcceptEula);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "daybreak-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            Assert.Empty(ServerStore.Load(dir).Servers);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Load_CorruptJson_ReturnsEmptyInsteadOfThrowing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "daybreak-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            // 列表文件损坏时不能连启动器一起崩,应回退空表(实例目录仍在,可手工恢复)
            File.WriteAllText(ServerStore.StorePath(dir), "{ this is not json");
            Assert.Empty(ServerStore.Load(dir).Servers);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(ServerType.Vanilla, "1.20.1", null, "1.20.1")]
    [InlineData(ServerType.Fabric, "1.20.1", "0.15.11", "1.20.1 · Fabric 0.15.11")]
    [InlineData(ServerType.Paper, "1.20.1", "196", "1.20.1 · Paper #196")]
    public void VersionDisplay_FormatsPerType(string type, string game, string? loader, string expected)
    {
        var instance = new ServerInstance
        {
            Name = "t",
            Type = type,
            GameVersion = game,
            LoaderVersion = loader,
        };

        Assert.Equal(expected, instance.VersionDisplay);
    }
}