using System.IO;
using System.Net;
using System.Net.Sockets;
using Daybreak.Core.Server;
using Xunit;

namespace Daybreak.Core.Tests;

/// <summary>ServerManager 的纯逻辑部分(参数拆分、名称校验、端口检测)。</summary>
public class ServerManagerTests
{
    [Fact]
    public void SplitArguments_HandlesQuotedPathsWithSpaces()
    {
        var args = ServerManager.SplitArguments(@"-XX:+UseG1GC ""C:\Program Files\java\bin\server.dll"" -Xmx2G");

        Assert.Equal(3, args.Count);
        Assert.Equal("-XX:+UseG1GC", args[0]);
        // 带空格的引号参数不能被拆成两段
        Assert.Equal(@"C:\Program Files\java\bin\server.dll", args[1]);
        Assert.Equal("-Xmx2G", args[2]);
    }

    [Fact]
    public void SplitArguments_EmptyOrWhitespace_ReturnsEmpty()
    {
        Assert.Empty(ServerManager.SplitArguments(""));
        Assert.Empty(ServerManager.SplitArguments("   "));
    }

    [Theory]
    [InlineData("我的生存服")]
    [InlineData("survival-1")]
    [InlineData("server_2026")]
    public void ValidateName_AcceptsNormalNames(string name)
    {
        // 不抛异常即通过
        ServerManager.ValidateName(name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad/name")]
    [InlineData("bad\\name")]
    [InlineData("bad:name")]
    [InlineData("bad*name")]
    [InlineData("bad?name")]
    [InlineData(".")]
    [InlineData("..")]
    public void ValidateName_RejectsInvalidNames(string name)
    {
        // 名称会直接作为目录名,必须挡住路径穿越与非法字符
        Assert.Throws<InvalidOperationException>(() => ServerManager.ValidateName(name));
    }

    [Fact]
    public void ValidateName_RejectsOverlyLongName()
    {
        Assert.Throws<InvalidOperationException>(
            () => ServerManager.ValidateName(new string('a', 51)));
    }

    [Fact]
    public void IsPortInUse_DetectsOccupiedPort()
    {
        // 真占一个端口,验证检测有效
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        try
        {
            Assert.True(ServerManager.IsPortInUse(port));
        }
        finally
        {
            listener.Stop();
        }

        // 释放后应恢复可用
        Assert.False(ServerManager.IsPortInUse(port));
    }

    [Fact]
    public void BuildArguments_IncludesMemoryAndNogui()
    {
        var manager = new ServerManager(
            new Daybreak.Core.Game.LaunchService(
                Path.Combine(Path.GetTempPath(), "daybreak-mgr-" + Guid.NewGuid().ToString("N")),
                Daybreak.Core.Game.DownloadMirror.Official),
            Daybreak.Core.Game.DownloadMirror.Official);

        var instance = new ServerInstance
        {
            Name = "t",
            Type = ServerType.Vanilla,
            GameVersion = "1.20.1",
            MinMemoryMb = 1024,
            MaxMemoryMb = 4096,
            LaunchTarget = "server.jar",
        };

        var args = manager.BuildArguments(instance, ".");

        Assert.Contains("-Xms1024M", args);
        Assert.Contains("-Xmx4096M", args);
        Assert.Contains("-jar", args);
        Assert.Contains("server.jar", args);
        // 无 GUI 环境不加 nogui 原版会尝试弹窗,在服务器/后台会失败
        Assert.Contains("nogui", args);
    }

    [Fact]
    public void BuildArguments_PutsCustomJvmArgsBeforeMemory()
    {
        var manager = new ServerManager(
            new Daybreak.Core.Game.LaunchService(
                Path.Combine(Path.GetTempPath(), "daybreak-mgr-" + Guid.NewGuid().ToString("N")),
                Daybreak.Core.Game.DownloadMirror.Official),
            Daybreak.Core.Game.DownloadMirror.Official);

        var instance = new ServerInstance
        {
            Name = "t",
            Type = ServerType.Paper,
            GameVersion = "1.20.1",
            LaunchTarget = "server.jar",
            JvmArguments = "-XX:+UseG1GC -XX:+ParallelRefProcEnabled",
        };

        var args = manager.BuildArguments(instance, ".");

        // Paper 官方推荐参数要在内存参数之前生效
        Assert.Equal("-XX:+UseG1GC", args[0]);
        Assert.True(args.IndexOf("-XX:+ParallelRefProcEnabled") < args.IndexOf("-Xms1024M"));
    }

    [Fact]
    public void ResolveInstanceDirectory_UsesGameDirectorySubfolder()
    {
        var gameDir = Path.Combine(Path.GetTempPath(), "daybreak-gd-" + Guid.NewGuid().ToString("N"));
        var manager = new ServerManager(
            new Daybreak.Core.Game.LaunchService(gameDir, Daybreak.Core.Game.DownloadMirror.Official),
            Daybreak.Core.Game.DownloadMirror.Official);

        var dir = manager.ResolveInstanceDirectory("我的服");

        // 需求:服务端放在游戏目录内的 daybreak-servers/<名称>/
        Assert.Equal(Path.Combine(gameDir, "daybreak-servers", "我的服"), dir);
        Assert.Equal(Path.Combine(gameDir, "daybreak-servers"), manager.ServersRoot);
    }

    /// <summary>
    /// Forge 1.17+ 的启动参数必须从 run.bat 的 @ 引用里提取。
    /// 曾经想过用 `java -jar run.bat`,那只会得到 "Invalid or corrupt jarfile"。
    /// </summary>
    [Fact]
    public void ExtractAtArguments_ParsesForgeRunBat()
    {
        var dir = Path.Combine(Path.GetTempPath(), "daybreak-runbat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var runBat = Path.Combine(dir, "run.bat");

        try
        {
            // 参数文件必须真实存在才会被采纳(见实现里的说明)
            File.WriteAllText(Path.Combine(dir, "user_jvm_args.txt"), "-Xmx2G");
            var libDir = Path.Combine(dir, "libraries", "net", "minecraftforge", "forge", "1.20.1-47.4.28");
            Directory.CreateDirectory(libDir);
            File.WriteAllText(Path.Combine(libDir, "win_args.txt"), "--launchTarget forge_server");

            // run.bat 的实际内容形态(Forge 1.17+ 生成,注意开头的 @echo off)
            File.WriteAllText(runBat, string.Join("\r\n",
                "@echo off",
                "REM Forge startup script",
                "java @user_jvm_args.txt @libraries/net/minecraftforge/forge/1.20.1-47.4.28/win_args.txt %*",
                "pause"));

            var args = ServerManager.ExtractAtArguments(runBat);

            // 必须恰好 2 个:@echo 里的 echo 不能被当成参数文件
            Assert.Equal(2, args.Count);
            Assert.Contains("@user_jvm_args.txt", args);
            Assert.Contains(args, a => a.Contains("win_args.txt"));
            Assert.DoesNotContain(args, a => a.Contains("echo"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ExtractAtArguments_IgnoresNonExistentFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "daybreak-runbat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var runBat = Path.Combine(dir, "run.bat");

        try
        {
            // 引用了不存在的文件:忽略掉,避免把无效参数传给 java
            File.WriteAllText(runBat, "java @missing_args.txt nogui");

            Assert.Empty(ServerManager.ExtractAtArguments(runBat));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BuildArguments_ForgeRunBat_UsesAtFilesNotJarFlag()
    {
        var dir = Path.Combine(Path.GetTempPath(), "daybreak-forgearg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            File.WriteAllText(Path.Combine(dir, "user_jvm_args.txt"), "-Xmx2G");
            var libDir = Path.Combine(dir, "libraries", "net", "minecraftforge", "forge", "1.20.1-47.4.28");
            Directory.CreateDirectory(libDir);
            File.WriteAllText(Path.Combine(libDir, "win_args.txt"), "--launchTarget forge_server");

            File.WriteAllText(Path.Combine(dir, "run.bat"), string.Join("\r\n",
                "@echo off",
                "java @user_jvm_args.txt @libraries/net/minecraftforge/forge/1.20.1-47.4.28/win_args.txt %*"));

            var manager = new ServerManager(
                new Daybreak.Core.Game.LaunchService(dir, Daybreak.Core.Game.DownloadMirror.Official),
                Daybreak.Core.Game.DownloadMirror.Official);

            var instance = new ServerInstance
            {
                Name = "t",
                Type = ServerType.Forge,
                GameVersion = "1.20.1",
                LaunchTarget = "run.bat",
            };

            var args = manager.BuildArguments(instance, dir);

            Assert.Contains(args, a => a.StartsWith('@') && a.Contains("user_jvm_args"));
            Assert.Contains(args, a => a.StartsWith('@') && a.Contains("win_args"));
            Assert.Contains("nogui", args);
            // 走 @ 文件时不能再用 -jar,否则必然启动失败
            Assert.DoesNotContain("-jar", args);
            Assert.DoesNotContain("run.bat", args);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BuildArguments_NonForge_StillUsesJarForm()
    {
        var manager = new ServerManager(
            new Daybreak.Core.Game.LaunchService(
                Path.Combine(Path.GetTempPath(), "daybreak-arg-" + Guid.NewGuid().ToString("N")),
                Daybreak.Core.Game.DownloadMirror.Official),
            Daybreak.Core.Game.DownloadMirror.Official);

        var instance = new ServerInstance
        {
            Name = "t",
            Type = ServerType.Vanilla,
            GameVersion = "1.20.1",
            LaunchTarget = "server.jar",
        };

        var args = manager.BuildArguments(instance, ".");

        // 非 Forge 路径不能被改动
        Assert.Contains("-jar", args);
        Assert.Contains("server.jar", args);
        Assert.Contains("nogui", args);
        Assert.DoesNotContain(args, a => a.StartsWith('@'));
    }
}