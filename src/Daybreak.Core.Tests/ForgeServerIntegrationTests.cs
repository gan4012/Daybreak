using System.IO;
using System.Net.Sockets;
using Daybreak.Core.Game;
using Daybreak.Core.Mod;
using Daybreak.Core.Server;
using Xunit;
using Xunit.Abstractions;

namespace Daybreak.Core.Tests;

/// <summary>
/// 阶段 3 端到端验证:Forge 服务端真的装起来并真的能开。
///
/// Forge 是四种服务端里唯一需要跑外部安装器(`--installServer`)的:
/// 安装器会自己下载 MC 服务端、拉依赖、执行 processors 打补丁,耗时且失败原因多样。
/// 同时验证两个容易出错的地方:
/// 1. 安装器产出的启动目标是 run.bat 而不是 jar,启动参数必须走 @ 参数文件
/// 2. 内存要写进 user_jvm_args.txt(run.bat 会读它),不能只加在命令行
/// </summary>
[Trait("Category", "Integration")]
public class ForgeServerIntegrationTests
{
    private const string GameVersion = "1.20.1";
    private const string InstanceName = "daybreak-itest-forge";
    private const int TestPort = 25596;

    private readonly ITestOutputHelper _output;

    public ForgeServerIntegrationTests(ITestOutputHelper output) => _output = output;

    private static string GameDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");

    [Fact]
    public async Task InstallAndStartForgeServer()
    {
        var launchService = new LaunchService(GameDir, DownloadMirror.Official);
        var manager = new ServerManager(launchService, DownloadMirror.Official);

        await CleanupAsync(manager);

        var progress = new Progress<LaunchProgress>(p =>
        {
            if (!string.IsNullOrEmpty(p.CurrentFile))
                _output.WriteLine($"  {p.CurrentFile}");
        });

        // 选一个 Forge 版本:优先推荐版
        var loaderService = new LoaderService(GameDir, DownloadMirror.Official);
        var forgeVersions = await loaderService.GetForgeVersionsAsync(GameVersion);
        Assert.NotEmpty(forgeVersions);
        var forge = forgeVersions.FirstOrDefault(v => v.Recommended) ?? forgeVersions[0];
        _output.WriteLine($"选用 Forge {forge.Name}");

        var instance = new ServerInstance
        {
            Name = InstanceName,
            Type = ServerType.Forge,
            GameVersion = GameVersion,
            LoaderVersion = forge.Name,
            Port = TestPort,
            MinMemoryMb = 1024,
            MaxMemoryMb = 2048,
        };

        try
        {
            _output.WriteLine($"=== 安装 Forge 服务端 {GameVersion}-{forge.Name}(跑 --installServer,耗时较长)===");
            await manager.CreateAsync(instance, progress);

            var dir = manager.ResolveInstanceDirectory(InstanceName);

            _output.WriteLine($"  启动目标 = {instance.LaunchTarget}");

            // Forge 1.17+ 应产出 run.bat
            Assert.Equal("run.bat", instance.LaunchTarget);
            Assert.True(File.Exists(Path.Combine(dir, "run.bat")), "run.bat 未生成");
            Assert.True(File.Exists(Path.Combine(dir, "user_jvm_args.txt")),
                "user_jvm_args.txt 未生成(内存参数需要写在这里)");

            // 内存必须真的写进了 user_jvm_args.txt,否则 Forge 会用默认内存
            var jvmArgs = File.ReadAllText(Path.Combine(dir, "user_jvm_args.txt"));
            Assert.Contains("-Xmx2048M", jvmArgs);
            Assert.Contains("-Xms1024M", jvmArgs);
            _output.WriteLine($"  user_jvm_args.txt 已写入内存参数");

            // ---- 关键:启动参数必须走 @ 参数文件,不能是 -jar run.bat ----
            var args = manager.BuildArguments(instance, dir);
            _output.WriteLine($"  启动参数 = java {string.Join(' ', args)}");

            Assert.Contains(args, a => a.StartsWith('@') && a.Contains("user_jvm_args"));
            Assert.Contains(args, a => a.StartsWith('@') && a.Contains("win_args"));
            Assert.Contains("nogui", args);
            Assert.DoesNotContain("-jar", args);
            Assert.DoesNotContain("run.bat", args);

            // @ 引用的文件必须真实存在,否则 java 会直接报错退出
            foreach (var atArg in args.Where(a => a.StartsWith('@')))
            {
                var referenced = Path.Combine(dir, atArg[1..]);
                Assert.True(File.Exists(referenced), $"@ 参数文件不存在:{referenced}");
            }
            _output.WriteLine("  ✓ @ 参数文件均存在");

            // ---- 启动 ----
            EulaService.Accept(dir);
            var process = await manager.StartAsync(instance, progress);

            var logLines = new List<string>();
            var doneReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            process.OutputReceived += line =>
            {
                lock (logLines)
                    logLines.Add(line);

                if (line.Contains("Done (", StringComparison.Ordinal))
                    doneReached.TrySetResult(true);

                if (line.Contains("Exception", StringComparison.Ordinal) ||
                    line.Contains("Error", StringComparison.Ordinal) ||
                    line.Contains("FAILED", StringComparison.Ordinal) ||
                    line.Contains("corrupt", StringComparison.OrdinalIgnoreCase))
                    _output.WriteLine($"  [服务端] {line}");
            };

            var ready = await Task.WhenAny(doneReached.Task, Task.Delay(TimeSpan.FromMinutes(5)));
            Assert.True(ready == doneReached.Task,
                $"Forge 服务端未在 5 分钟内就绪。日志尾部:\n{LastLines(logLines, 25)}");

            _output.WriteLine($"  ✓ 就绪:{LastLines(logLines, 3)}");

            Assert.True(IsPortListening(TestPort), $"端口 {TestPort} 未在监听");
            _output.WriteLine($"  ✓ 端口 {TestPort} 正在监听");

            // Forge 特有的日志行能进一步确认 Mod 加载器真的起来了
            lock (logLines)
            {
                var hasForgeLine = logLines.Any(l =>
                    l.Contains("forge", StringComparison.OrdinalIgnoreCase));
                Assert.True(hasForgeLine, "日志里没有 Forge 相关行,可能没用 Forge 启动");
            }

            await manager.StopAsync(InstanceName);
            Assert.False(manager.IsRunning(InstanceName));
            Assert.True(Directory.Exists(Path.Combine(dir, "world")), "世界目录未生成");

            _output.WriteLine("✅ Forge 服务端验证通过");
        }
        finally
        {
            await CleanupAsync(manager);
        }
    }

    private static async Task CleanupAsync(ServerManager manager)
    {
        try
        {
            if (manager.IsRunning(InstanceName))
                await manager.StopAsync(InstanceName);
            await manager.DeleteAsync(InstanceName, deleteFiles: true);
        }
        catch
        {
            // 清理失败不影响断言
        }
    }

    private static bool IsPortListening(int port)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync("127.0.0.1", port);
            return task.Wait(TimeSpan.FromSeconds(5)) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static string LastLines(List<string> lines, int count)
    {
        lock (lines)
            return string.Join("\n", lines.TakeLast(count).Select(l => "    " + l));
    }
}