using System.Diagnostics;
using System.IO;
using System.Text;

namespace Daybreak.Core.Server;

/// <summary>服务端进程状态变化事件参数。</summary>
public class ServerProcessEventArgs : EventArgs
{
    public required string InstanceName { get; init; }
    public required ServerState State { get; init; }
    public int? ExitCode { get; init; }
    public string? Message { get; init; }
}

/// <summary>
/// 服务端进程管理:启动 / 停止 / 控制台输入输出。
///
/// 与游戏启动的关键差异:
/// - 服务端**必须能接收 stdin**(stop / op / whitelist 等命令),所以要持有进程句柄
/// - 服务端日志量远大于游戏,事件必须由调用方节流后再上 UI
/// - 停服要走 `stop` 命令而非杀进程,否则世界可能损坏
/// </summary>
public class ServerProcess : IDisposable
{
    private readonly object _gate = new();
    private Process? _process;
    private StreamWriter? _stdin;
    private bool _disposed;

    public string InstanceName { get; }
    public ServerState State { get; private set; } = ServerState.Stopped;
    public int? ProcessId { get; private set; }

    /// <summary>服务端输出一行(已去掉行尾)。调用方必须节流。</summary>
    public event Action<string>? OutputReceived;

    /// <summary>状态变化。</summary>
    public event Action<ServerProcessEventArgs>? StateChanged;

    public ServerProcess(string instanceName) => InstanceName = instanceName;

    /// <summary>
    /// 启动服务端。
    /// </summary>
    /// <param name="javaPath">java.exe 完整路径</param>
    /// <param name="workingDirectory">服务端实例目录(必须是它,否则 server.properties/world 会生成到别处)</param>
    /// <param name="arguments">完整参数列表(不含 java 本身)</param>
    public void Start(string javaPath, string workingDirectory, IReadOnlyList<string> arguments)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ServerProcess));

        lock (_gate)
        {
            if (_process is { HasExited: false })
                throw new InvalidOperationException($"服务端「{InstanceName}」已在运行中");
        }

        Directory.CreateDirectory(workingDirectory);

        var psi = new ProcessStartInfo
        {
            FileName = javaPath,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,     // 必须:要靠 stdin 发 stop 命令
            RedirectStandardOutput = true,    // 必须:否则日志不可见
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // 服务端输出含中文时用 UTF-8 解码,默认编码会乱码
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                OutputReceived?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                OutputReceived?.Invoke(e.Data);
        };
        process.Exited += (_, _) => OnProcessExited(process);

        SetState(ServerState.Starting, "正在启动…");

        if (!process.Start())
        {
            SetState(ServerState.Stopped, "进程启动失败");
            throw new InvalidOperationException("无法启动服务端进程");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        lock (_gate)
        {
            _process = process;
            _stdin = process.StandardInput;
            _stdin.AutoFlush = true;   // 命令要立刻送达,不能等缓冲
            ProcessId = process.Id;
        }

        SetState(ServerState.Running, "运行中");
    }

    /// <summary>向服务端控制台发送一条命令(不含换行)。</summary>
    public bool SendCommand(string command)
    {
        lock (_gate)
        {
            if (_stdin is null || _process is not { HasExited: false })
                return false;

            try
            {
                _stdin.WriteLine(command);
                return true;
            }
            catch (IOException)
            {
                // 进程刚退出时写管道会抛,不算异常情况
                return false;
            }
        }
    }

    /// <summary>
    /// 优雅停服:优先发 `stop` 命令,超时后再强杀。
    /// 直接杀进程可能导致世界文件损坏,所以必须先给机会让它正常保存退出。
    /// </summary>
    public async Task StopAsync(TimeSpan? gracefulTimeout = null, CancellationToken ct = default)
    {
        var process = GetLiveProcess();
        if (process is null)
        {
            SetState(ServerState.Stopped, "已停止");
            return;
        }

        var timeout = gracefulTimeout ?? TimeSpan.FromSeconds(30);
        SetState(ServerState.Stopping, "正在保存并关闭…");

        // 1. 先礼:发 stop 命令
        if (SendCommand("stop"))
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);
                await process.WaitForExitAsync(cts.Token);
                return;
            }
            catch (OperationCanceledException)
            {
                // 超时未退出,落到下面强杀
            }
        }

        // 2. 后兵:超时了才强杀
        await ForceKillAsync(process);
    }

    /// <summary>强制结束进程(不推荐,会造成世界未保存)。</summary>
    public async Task ForceKillAsync(Process? known = null)
    {
        var process = known ?? GetLiveProcess();
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);

            await process.WaitForExitAsync();
        }
        catch (Exception)
        {
            // 进程可能刚好自己退出了,这种情况无需处理
        }
    }

    private Process? GetLiveProcess()
    {
        lock (_gate)
        {
            if (_process is null)
                return null;

            try
            {
                return _process.HasExited ? null : _process;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    private void OnProcessExited(Process process)
    {
        int? exitCode = null;
        try
        {
            exitCode = process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            // 进程句柄已失效,拿不到退出码
        }

        lock (_gate)
        {
            ProcessId = null;
            _stdin = null;
        }

        SetState(
            ServerState.Stopped,
            exitCode is null or 0 ? "已停止" : $"异常退出(退出码 {exitCode})",
            exitCode);
    }

    private void SetState(ServerState state, string? message = null, int? exitCode = null)
    {
        State = state;
        StateChanged?.Invoke(new ServerProcessEventArgs
        {
            InstanceName = InstanceName,
            State = state,
            ExitCode = exitCode,
            Message = message,
        });
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        var process = GetLiveProcess();
        if (process is not null)
        {
            // 启动器退出时不能让服务端变孤儿进程,先尝试正常停
            try
            {
                SendCommand("stop");
                if (!process.WaitForExit(5000))
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // 释放阶段的清理失败不应影响启动器退出
            }
        }

        _process?.Dispose();
        _process = null;
        _stdin = null;
    }
}