using System.IO;
using System.Net.Sockets;
using Daybreak.Core.Game;
using Daybreak.Core.Server;
using Xunit;
using Xunit.Abstractions;

namespace Daybreak.Core.Tests;

/// <summary>
/// 阶段 1 端到端验证:真的装一个原版服务端、真的启动它、真的连上去。
///
/// 用真实游戏目录(而不是临时目录)是有意为之:
/// java-runtime 会缓存到 &lt;游戏目录&gt;/runtime 下,后续跑测试与用户实际开服都能复用,
/// 否则每次都要重新下 200MB+ 的 Java 运行时。
/// 测试实例用固定前缀命名并在结束时清理,不会残留到用户的服务器列表里。
/// </summary>
[Trait("Category", "Integration")]
public class VanillaServerIntegrationTests
{
    private const string TestInstanceName = "daybreak-itest-vanilla";
    private const string GameVersion = "1.20.1";
    private const int TestPort = 25599;   // 避开默认 25565,防止与用户已有服务端冲突

    private readonly ITestOutputHelper _output;

    public VanillaServerIntegrationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task CreateStartConnectAndStopVanillaServer()
    {
        var gameDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");

        var launchService = new LaunchService(gameDir, DownloadMirror.Official);
        var manager = new ServerManager(launchService, DownloadMirror.Official);

        // 清掉上次可能残留的测试实例
        await CleanupAsync(manager);

        var progress = new Progress<LaunchProgress>(p =>
        {
            if (!string.IsNullOrEmpty(p.CurrentFile))
                _output.WriteLine($"  {p.CurrentFile}");
        });

        var instance = new ServerInstance
        {
            Name = TestInstanceName,
            Type = ServerType.Vanilla,
            GameVersion = GameVersion,
            Port = TestPort,
            MinMemoryMb = 1024,
            MaxMemoryMb = 2048,
        };

        try
        {
            // ---- 1. 安装 ----
            _output.WriteLine($"创建原版服务端 {GameVersion}(端口 {TestPort})…");
            await manager.CreateAsync(instance, progress);

            var dir = manager.ResolveInstanceDirectory(TestInstanceName);
            var jarPath = Path.Combine(dir, "server.jar");

            Assert.True(File.Exists(jarPath), "server.jar 未下载");
            var jarSize = new FileInfo(jarPath).Length;
            _output.WriteLine($"server.jar = {jarSize / 1024 / 1024} MB");
            Assert.True(jarSize > 20_000_000, "server.jar 体积异常,可能下载不完整");

            Assert.True(File.Exists(Path.Combine(dir, "server.properties")), "server.properties 未生成");
            Assert.Equal(TestPort, ServerProperties.Load(Path.Combine(dir, "server.properties"))
                .GetInt("server-port", 0));

            // ---- 2. 未同意 EULA 时必须拒绝启动 ----
            _output.WriteLine("验证未同意 EULA 时拒绝启动…");
            var eulaError = await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.StartAsync(instance, progress));
            Assert.Contains("EULA", eulaError.Message);
            _output.WriteLine($"  ✓ 已拦截:{eulaError.Message}");

            // ---- 3. 用户显式同意后启动(这一步模拟用户在 UI 上勾选)----
            _output.WriteLine("模拟用户同意 EULA 并启动服务端…");
            EulaService.Accept(dir);
            Assert.True(EulaService.IsAccepted(dir));

            var process = await manager.StartAsync(instance, progress);

            var logLines = new List<string>();
            var doneReached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            process.OutputReceived += line =>
            {
                lock (logLines)
                    logLines.Add(line);

                // 原版服务端就绪的标志行是 'Done (x.xxxs)! For help, type "help"'
                if (line.Contains("Done (", StringComparison.Ordinal))
                    doneReached.TrySetResult(true);

                if (line.Contains("FAILED TO BIND", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Exception", StringComparison.Ordinal))
                    _output.WriteLine($"[服务端] {line}");
            };

            // 等就绪,最长 3 分钟(首次启动要生成世界)
            var readyTask = await Task.WhenAny(doneReached.Task, Task.Delay(TimeSpan.FromMinutes(3)));
            Assert.True(readyTask == doneReached.Task,
                $"服务端未在 3 分钟内就绪。最后几行日志:\n{LastLines(logLines, 15)}");

            _output.WriteLine($"  ✓ 服务端就绪。日志尾部:\n{LastLines(logLines, 5)}");

            // ---- 4. 端口真的在监听 ----
            Assert.True(IsPortListening(TestPort), $"端口 {TestPort} 未在监听");
            _output.WriteLine($"  ✓ 端口 {TestPort} 正在监听");

            // ---- 5. 控制台命令可用(验证 stdin 链路)----
            _output.WriteLine("发送 list 命令验证控制台…");
            Assert.True(manager.SendCommand(TestInstanceName, "list"), "命令发送失败");

            var listAnswered = await WaitForLogAsync(logLines,
                l => l.Contains("There are", StringComparison.OrdinalIgnoreCase) ||
                     l.Contains("players online", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(15));

            Assert.True(listAnswered, $"list 命令未得到响应。日志尾部:\n{LastLines(logLines, 10)}");
            _output.WriteLine("  ✓ 控制台命令有响应");

            // ---- 6. 优雅停服,世界要落盘 ----
            _output.WriteLine("优雅停服…");
            var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.StateChanged += e =>
            {
                if (e.State == ServerState.Stopped)
                    stopped.TrySetResult(true);
            };

            await manager.StopAsync(TestInstanceName);

            var stopTask = await Task.WhenAny(stopped.Task, Task.Delay(TimeSpan.FromSeconds(20)));
            Assert.True(stopTask == stopped.Task, "停服后未收到 Stopped 状态");

            Assert.False(manager.IsRunning(TestInstanceName));
            // 正常停服会保存世界,world 目录必须存在
            Assert.True(Directory.Exists(Path.Combine(dir, "world")), "世界目录未生成");
            _output.WriteLine("  ✓ 已优雅停服,世界已保存");

            _output.WriteLine("✅ 阶段 1 端到端验证通过:安装 → EULA 拦截 → 启动 → 端口监听 → 控制台命令 → 优雅停服");
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
            if (manager.IsRunning(TestInstanceName))
                await manager.StopAsync(TestInstanceName);

            await manager.DeleteAsync(TestInstanceName, deleteFiles: true);
        }
        catch
        {
            // 清理失败不影响断言结果
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

    private static async Task<bool> WaitForLogAsync(
        List<string> lines, Func<string, bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (lines)
            {
                if (lines.Any(predicate))
                    return true;
            }
            await Task.Delay(300);
        }
        return false;
    }

    private static string LastLines(List<string> lines, int count)
    {
        lock (lines)
            return string.Join("\n", lines.TakeLast(count).Select(l => "    " + l));
    }
}