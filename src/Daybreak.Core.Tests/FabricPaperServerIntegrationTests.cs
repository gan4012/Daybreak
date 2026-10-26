using System.IO;
using System.Net.Sockets;
using Daybreak.Core.Game;
using Daybreak.Core.Server;
using Xunit;
using Xunit.Abstractions;

namespace Daybreak.Core.Tests;

/// <summary>
/// 阶段 2 端到端验证:Fabric 与 Paper 服务端真的装起来、真的能开。
///
/// Paper 顺带验证 SHA256 校验链路(Paper 只提供 sha256,DownloadEngine 原生只做 sha1)。
/// Fabric 顺带验证它依赖的原版 server.jar 被一并下齐
/// (fabric-server-launch.jar 没有原版 jar 会在首次启动时报 server.jar not found)。
/// </summary>
[Trait("Category", "Integration")]
public class FabricPaperServerIntegrationTests
{
    private const string GameVersion = "1.20.1";
    private readonly ITestOutputHelper _output;

    public FabricPaperServerIntegrationTests(ITestOutputHelper output) => _output = output;

    private static string GameDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");

    [Fact]
    public async Task InstallAndStartFabricServer()
    {
        await RunServerScenarioAsync(
            instanceName: "daybreak-itest-fabric",
            type: ServerType.Fabric,
            port: 25598,
            expectFile: "fabric-server-launch.jar",
            extraChecks: dir =>
            {
                // Fabric 必须同时具备原版 server.jar,否则首次启动会失败
                Assert.True(File.Exists(Path.Combine(dir, "server.jar")),
                    "Fabric 实例缺少原版 server.jar(fabric-server-launch.jar 需要它来做运行时补丁)");
                Assert.True(File.Exists(Path.Combine(dir, "fabric-server-launcher.properties")),
                    "缺少 fabric-server-launcher.properties");
            });
    }

    [Fact]
    public async Task InstallAndStartPaperServer()
    {
        await RunServerScenarioAsync(
            instanceName: "daybreak-itest-paper",
            type: ServerType.Paper,
            port: 25597,
            expectFile: "server.jar",
            extraChecks: dir =>
            {
                // Paper 的核心应该比原版大(内含补丁后的服务端),太小说明下错了文件
                var jar = new FileInfo(Path.Combine(dir, "server.jar"));
                Assert.True(jar.Length > 20_000_000,
                    $"Paper 核心体积异常:{jar.Length / 1024 / 1024} MB");
            });
    }

    private async Task RunServerScenarioAsync(
        string instanceName, string type, int port,
        string expectFile, Action<string>? extraChecks)
    {
        var launchService = new LaunchService(GameDir, DownloadMirror.Official);
        var manager = new ServerManager(launchService, DownloadMirror.Official);

        await CleanupAsync(manager, instanceName);

        var progress = new Progress<LaunchProgress>(p =>
        {
            if (!string.IsNullOrEmpty(p.CurrentFile))
                _output.WriteLine($"  {p.CurrentFile}");
        });

        var instance = new ServerInstance
        {
            Name = instanceName,
            Type = type,
            GameVersion = GameVersion,
            Port = port,
            MinMemoryMb = 1024,
            MaxMemoryMb = 2048,
        };

        try
        {
            _output.WriteLine($"=== {ServerType.DisplayName(type)} {GameVersion} ===");
            await manager.CreateAsync(instance, progress);

            var dir = manager.ResolveInstanceDirectory(instanceName);

            Assert.True(File.Exists(Path.Combine(dir, expectFile)), $"缺少 {expectFile}");
            Assert.NotNull(instance.LaunchTarget);
            _output.WriteLine($"  启动目标 = {instance.LaunchTarget}");

            extraChecks?.Invoke(dir);

            // Paper 应记录构建号;Fabric 应记录加载器版本
            Assert.False(string.IsNullOrEmpty(instance.LoaderVersion),
                $"{type} 未记录加载器/构建版本");

            if (type == ServerType.Paper)
            {
                _output.WriteLine($"  Paper 构建号 = {instance.LoaderVersion}");
                // Paper 的推荐 JVM 参数应被自动套用
                if (!string.IsNullOrEmpty(instance.JvmArguments))
                    _output.WriteLine($"  已套用 Paper 推荐 JVM 参数:{instance.JvmArguments}");
            }
            else
            {
                _output.WriteLine($"  Fabric 加载器 = {instance.LoaderVersion}");
            }

            // 启动并等待就绪
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
                    line.Contains("FAILED", StringComparison.Ordinal))
                    _output.WriteLine($"  [服务端] {line}");
            };

            var ready = await Task.WhenAny(doneReached.Task, Task.Delay(TimeSpan.FromMinutes(4)));
            Assert.True(ready == doneReached.Task,
                $"{type} 服务端未在 4 分钟内就绪。日志尾部:\n{LastLines(logLines, 20)}");

            _output.WriteLine($"  ✓ 就绪:{LastLines(logLines, 2)}");

            Assert.True(IsPortListening(port), $"端口 {port} 未在监听");
            _output.WriteLine($"  ✓ 端口 {port} 正在监听");

            await manager.StopAsync(instanceName);
            Assert.False(manager.IsRunning(instanceName));
            Assert.True(Directory.Exists(Path.Combine(dir, "world")), "世界目录未生成");

            _output.WriteLine($"✅ {ServerType.DisplayName(type)} 服务端验证通过");
        }
        finally
        {
            await CleanupAsync(manager, instanceName);
        }
    }

    private static async Task CleanupAsync(ServerManager manager, string name)
    {
        try
        {
            if (manager.IsRunning(name))
                await manager.StopAsync(name);
            await manager.DeleteAsync(name, deleteFiles: true);
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