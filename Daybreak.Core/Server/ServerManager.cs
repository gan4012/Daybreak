using System.IO;
using System.Text.RegularExpressions;
using Daybreak.Core.Game;

namespace Daybreak.Core.Server;

/// <summary>
/// 服务端管理门面:实例增删改查 + 启动命令组装 + 运行中进程托管。
///
/// 存储布局(按需求:统一放游戏目录内):
///   &lt;游戏目录&gt;/daybreak-servers/&lt;实例名&gt;/     ← 每个实例的完整服务端目录
///   &lt;游戏目录&gt;/daybreak-servers.json          ← 实例列表索引
/// </summary>
public class ServerManager
{
    private readonly LaunchService _launchService;
    private readonly string _gameDirectory;
    private readonly string _mirror;

    /// <summary>运行中的进程(按实例名索引)。</summary>
    private readonly Dictionary<string, ServerProcess> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public ServerManager(LaunchService launchService, string mirror)
    {
        _launchService = launchService;
        _gameDirectory = launchService.ResolveGameDirectory();
        _mirror = DownloadMirror.IsValid(mirror) ? mirror : DownloadMirror.Official;
    }

    /// <summary>实例根目录:&lt;游戏目录&gt;/daybreak-servers。</summary>
    public string ServersRoot => Path.Combine(_gameDirectory, "daybreak-servers");

    public string ResolveInstanceDirectory(string name) => Path.Combine(ServersRoot, name);

    // ===== 增删查 =====

    public List<ServerInstance> List()
    {
        var store = ServerStore.Load(_gameDirectory);
        foreach (var instance in store.Servers)
            instance.State = GetState(instance.Name);
        return store.Servers;
    }

    /// <summary>
    /// 创建实例:建目录、写默认 server.properties、下载服务端核心。
    /// </summary>
    public async Task<ServerInstance> CreateAsync(
        ServerInstance instance,
        IProgress<LaunchProgress>? progress = null,
        CancellationToken ct = default)
    {
        ValidateName(instance.Name);

        if (List().Any(s => s.Name.Equals(instance.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"已存在同名服务端「{instance.Name}」");

        var dir = ResolveInstanceDirectory(instance.Name);
        Directory.CreateDirectory(dir);

        // 1. 装服务端核心
        if (instance.Type == ServerType.Forge)
        {
            var javaPath = await ResolveJavaAsync(instance, progress);
            var installer = new ForgeServerInstaller(_mirror);

            var target = await installer.InstallAsync(
                instance.GameVersion, instance.LoaderVersion ?? "", dir, javaPath, progress, ct);

            instance.LaunchTarget = target;

            // Forge 1.17+ 的内存要写进 user_jvm_args.txt(run.bat 会读它)
            if (target.Equals("run.bat", StringComparison.OrdinalIgnoreCase))
                ForgeServerInstaller.WriteJvmArgs(dir, instance.MinMemoryMb, instance.MaxMemoryMb, instance.JvmArguments);
        }
        else
        {
            var downloader = new ServerDownloader(_mirror);
            instance.LaunchTarget = await downloader.InstallAsync(instance, dir, progress, ct);
        }

        // 2. 写 server.properties(保留已有文件,避免覆盖用户改动)
        var propsPath = Path.Combine(dir, "server.properties");
        if (!File.Exists(propsPath))
        {
            ServerProperties.CreateDefault(instance.Port, motd: $"Daybreak · {instance.Name}")
                .Save(propsPath);
        }

        // 3. 落盘索引
        var store = ServerStore.Load(_gameDirectory);
        store.Servers.Add(instance);
        store.Save(_gameDirectory);

        return instance;
    }

    /// <summary>更新实例元数据(不重新下载核心)。</summary>
    public void Update(ServerInstance instance)
    {
        var store = ServerStore.Load(_gameDirectory);
        var index = store.Servers.FindIndex(s => s.Name.Equals(instance.Name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            store.Servers[index] = instance;
        else
            store.Servers.Add(instance);

        store.Save(_gameDirectory);
    }

    /// <summary>
    /// 删除实例。
    /// 默认保留世界存档(把 world 目录移到回收位置太复杂,这里改为:deleteFiles=false 时只从列表移除)。
    /// </summary>
    public async Task DeleteAsync(string name, bool deleteFiles, CancellationToken ct = default)
    {
        await StopAsync(name, ct);

        var store = ServerStore.Load(_gameDirectory);
        store.Servers.RemoveAll(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        store.Save(_gameDirectory);

        if (!deleteFiles)
            return;

        var dir = ResolveInstanceDirectory(name);
        if (!Directory.Exists(dir))
            return;

        // 服务端目录里可能还有 JAVA 进程占用的文件,删不掉时给出人话提示
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"删除目录失败(可能仍有文件被占用,请确认服务端已停止):{ex.Message}", ex);
        }
    }

    // ===== 启停 =====

    public ServerState GetState(string name)
    {
        lock (_gate)
        {
            return _running.TryGetValue(name, out var process) ? process.State : ServerState.Stopped;
        }
    }

    public bool IsRunning(string name) => GetState(name) is ServerState.Running or ServerState.Starting;

    /// <summary>获取运行中的进程对象(用于订阅日志/状态事件)。</summary>
    public ServerProcess? GetProcess(string name)
    {
        lock (_gate)
        {
            return _running.TryGetValue(name, out var process) ? process : null;
        }
    }

    /// <summary>启动服务端,返回进程对象(供 UI 订阅输出)。</summary>
    public async Task<ServerProcess> StartAsync(
        ServerInstance instance,
        IProgress<LaunchProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (IsRunning(instance.Name))
            throw new InvalidOperationException($"服务端「{instance.Name}」已在运行中");

        var dir = ResolveInstanceDirectory(instance.Name);
        if (!Directory.Exists(dir))
            throw new InvalidOperationException($"服务端目录不存在:{dir}");

        // EULA 必须已同意,否则原版服务端会直接退出(这是合规检查,不是可选项)
        if (!EulaService.IsAccepted(dir))
            throw new InvalidOperationException(
                "尚未同意 Minecraft EULA。请先在服务端设置中阅读并勾选同意,否则服务端会立即退出。");

        // 端口占用预检:不查的话用户只会看到一句 "FAILED TO BIND TO PORT"
        var portInUse = IsPortInUse(instance.Port);
        if (portInUse)
            throw new InvalidOperationException(
                $"端口 {instance.Port} 已被占用。请在服务端设置里换一个端口,或关闭占用该端口的程序。");

        var javaPath = await ResolveJavaAsync(instance, progress);
        var arguments = BuildArguments(instance, dir);

        progress?.Report(new LaunchProgress
        {
            CurrentFile = $"启动 {instance.Name}({instance.VersionDisplay})…",
        });

        var process = new ServerProcess(instance.Name);
        process.Start(javaPath, dir, arguments);

        lock (_gate)
        {
            if (_running.TryGetValue(instance.Name, out var old))
                old.Dispose();
            _running[instance.Name] = process;
        }

        instance.LastStartedAt = DateTime.Now;
        Update(instance);

        return process;
    }

    public async Task StopAsync(string name, CancellationToken ct = default)
    {
        ServerProcess? process;
        lock (_gate)
        {
            _running.TryGetValue(name, out process);
        }

        if (process is null)
            return;

        await process.StopAsync(ct: ct);

        lock (_gate)
        {
            _running.Remove(name);
        }

        process.Dispose();
    }

    /// <summary>向运行中的服务端发送控制台命令。</summary>
    public bool SendCommand(string name, string command)
    {
        var process = GetProcess(name);
        return process?.SendCommand(command) ?? false;
    }

    // ===== 命令组装 =====

    /// <summary>
    /// 组装启动参数。Forge 1.17+ 走 run.bat(参数由它的 args 文件决定),
    /// 其余情况是标准的 java -Xmx -jar 形式。
    /// </summary>
    public List<string> BuildArguments(ServerInstance instance, string dir)
    {
        var args = new List<string>();

        // 内存与自定义 JVM 参数
        if (!string.IsNullOrWhiteSpace(instance.JvmArguments))
            args.AddRange(SplitArguments(instance.JvmArguments));

        args.Add($"-Xms{instance.MinMemoryMb}M");
        args.Add($"-Xmx{instance.MaxMemoryMb}M");

        // 服务端在无 GUI 环境必须加 nogui,否则原版会尝试弹窗
        args.Add("-jar");
        args.Add(instance.LaunchTarget ?? "server.jar");
        args.Add("nogui");

        return args;
    }

    /// <summary>把一行参数拆成数组,正确处理引号(路径常带空格)。</summary>
    public static List<string> SplitArguments(string raw)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(raw))
            return result;

        foreach (Match match in Regex.Matches(raw, @"[^\s""]+|""([^""]*)"""))
        {
            var value = match.Groups[1].Success ? match.Groups[1].Value : match.Value;
            if (!string.IsNullOrEmpty(value))
                result.Add(value);
        }

        return result;
    }

    /// <summary>解析服务端应使用的 Java:优先实例指定,否则按游戏版本要求自动匹配。</summary>
    private async Task<string> ResolveJavaAsync(ServerInstance instance, IProgress<LaunchProgress>? progress)
    {
        if (!string.IsNullOrEmpty(instance.JavaPath) && File.Exists(instance.JavaPath))
            return instance.JavaPath;

        // 复用客户端那套逻辑:版本 JSON 声明的 javaVersion 组件优先
        var javaPath = await _launchService.EnsureJavaAsync(instance.GameVersion, null, progress);
        if (string.IsNullOrEmpty(javaPath) || !File.Exists(javaPath))
            throw new InvalidOperationException(
                $"找不到适配 {instance.GameVersion} 的 Java 运行时。请在设置中指定 Java 路径。");

        return javaPath;
    }

    /// <summary>端口是否已被占用。</summary>
    public static bool IsPortInUse(int port)
    {
        try
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return false;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return true;
        }
    }

    /// <summary>实例名合法性:会作为目录名,必须过滤非法字符。</summary>
    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("服务端名称不能为空");

        if (name.Length > 50)
            throw new InvalidOperationException("服务端名称不能超过 50 个字符");

        var invalid = Path.GetInvalidFileNameChars();
        if (name.IndexOfAny(invalid) >= 0)
            throw new InvalidOperationException(@"名称不能包含 \ / : * ? "" < > | 等字符");

        if (name is "." or "..")
            throw new InvalidOperationException("名称不合法");
    }

    /// <summary>停止所有本启动器托管的服务端(启动器退出时调用)。</summary>
    public async Task StopAllAsync()
    {
        List<string> names;
        lock (_gate)
        {
            names = [.. _running.Keys];
        }

        foreach (var name in names)
        {
            try
            {
                await StopAsync(name);
            }
            catch (Exception)
            {
                // 退出流程里单个服务端关不掉不应阻断其它实例
            }
        }
    }
}