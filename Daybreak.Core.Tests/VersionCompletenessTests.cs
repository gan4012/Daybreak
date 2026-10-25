using System.IO;
using Daybreak.Core.Game;
using Xunit;

namespace Daybreak.Core.Tests;

/// <summary>
/// 版本完整性判断。
///
/// 这个测试来自一个真实 bug:安装过程中 CmlLib 会**很早就写入版本 JSON**,
/// 而我们原先用"目录里有 json"判定"已安装",导致列表一边显示「已安装」
/// 一边还在下载资源,用户看到的是"上面说装好了、下面还在跑"。
/// </summary>
public class VersionCompletenessTests
{
    private static string NewGameDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "daybreak-vc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "versions"));
        return dir;
    }

    private static void WriteVersion(string gameDir, string name, string json, bool createJar)
    {
        var dir = Path.Combine(gameDir, "versions", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + ".json"), json);
        if (createJar)
            File.WriteAllBytes(Path.Combine(dir, name + ".jar"), new byte[2048]);
    }

    [Fact]
    public void IsVersionComplete_OnlyJsonNoJar_IsFalse()
    {
        var gameDir = NewGameDir();
        try
        {
            // 这正是安装中途的状态:json 已落地,jar 还没下完
            WriteVersion(gameDir, "1.21.1", """{"id":"1.21.1","libraries":[]}""", createJar: false);

            var service = new LaunchService(gameDir, DownloadMirror.Official);
            Assert.False(service.IsVersionComplete("1.21.1"));
        }
        finally
        {
            Directory.Delete(gameDir, recursive: true);
        }
    }

    [Fact]
    public void IsVersionComplete_JsonAndJar_IsTrue()
    {
        var gameDir = NewGameDir();
        try
        {
            WriteVersion(gameDir, "1.21.1", """{"id":"1.21.1","libraries":[]}""", createJar: true);

            var service = new LaunchService(gameDir, DownloadMirror.Official);
            Assert.True(service.IsVersionComplete("1.21.1"));
        }
        finally
        {
            Directory.Delete(gameDir, recursive: true);
        }
    }

    [Fact]
    public void IsVersionComplete_PartFilePresent_IsFalse()
    {
        var gameDir = NewGameDir();
        try
        {
            WriteVersion(gameDir, "1.21.1", """{"id":"1.21.1","libraries":[]}""", createJar: true);

            // 有 .part 残留 = 上次下载没完成,不能算装好
            File.WriteAllBytes(
                Path.Combine(gameDir, "versions", "1.21.1", "something.jar.part"), new byte[16]);

            var service = new LaunchService(gameDir, DownloadMirror.Official);
            Assert.False(service.IsVersionComplete("1.21.1"));
        }
        finally
        {
            Directory.Delete(gameDir, recursive: true);
        }
    }

    [Fact]
    public void IsVersionComplete_LoaderVersionWithInheritance_UsesParentJar()
    {
        var gameDir = NewGameDir();
        try
        {
            // 原版有 jar
            WriteVersion(gameDir, "1.20.1", """{"id":"1.20.1","libraries":[]}""", createJar: true);

            // 加载器版本只有 json + inheritsFrom,jar 由父版本提供(这是正常形态)
            WriteVersion(gameDir, "1.20.1-forge-47.4.28",
                """{"id":"1.20.1-forge-47.4.28","inheritsFrom":"1.20.1","libraries":[]}""",
                createJar: false);

            var service = new LaunchService(gameDir, DownloadMirror.Official);
            Assert.True(service.IsVersionComplete("1.20.1-forge-47.4.28"));
        }
        finally
        {
            Directory.Delete(gameDir, recursive: true);
        }
    }

    [Fact]
    public void IsVersionComplete_LoaderVersionWithMissingParent_IsFalse()
    {
        var gameDir = NewGameDir();
        try
        {
            // 父版本不存在 → 找不到 jar,不算完整
            WriteVersion(gameDir, "1.20.1-forge-47.4.28",
                """{"id":"1.20.1-forge-47.4.28","inheritsFrom":"1.20.1","libraries":[]}""",
                createJar: false);

            var service = new LaunchService(gameDir, DownloadMirror.Official);
            Assert.False(service.IsVersionComplete("1.20.1-forge-47.4.28"));
        }
        finally
        {
            Directory.Delete(gameDir, recursive: true);
        }
    }

    [Fact]
    public void IsVersionComplete_NonexistentVersion_IsFalse()
    {
        var gameDir = NewGameDir();
        try
        {
            var service = new LaunchService(gameDir, DownloadMirror.Official);
            Assert.False(service.IsVersionComplete("does-not-exist"));
        }
        finally
        {
            Directory.Delete(gameDir, recursive: true);
        }
    }
}