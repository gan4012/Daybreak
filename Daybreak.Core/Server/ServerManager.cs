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
    /// 组装启动参数。
    ///
    /// 两种情况:
    /// 1. 普通服务端(Vanilla/Fabric/Paper,以及老版本 Forge)→ `java -Xmx.. -jar &lt;jar&gt; nogui`
    /// 2. **Forge 1.17+** → 安装器生成的是 run.bat,真正的启动命令是
    ///    `java @user_jvm_args.txt @libraries/.../win_args.txt nogui`,
    ///    参数来自两个 @ 文件(内存也要写在 user_jvm_args.txt 里,不再是命令行)。
    ///    这里直接读 run.bat 提取 @ 引用,而不是硬编码路径——Forge 不同小版本的
    ///    libraries 目录结构会变,硬编码迟早失效。
    /// </summary>
    public List<string> BuildArguments(ServerInstance instance, string dir)
    {
        var args = new List<string>();
        var launchTarget = instance.LaunchTarget ?? "server.jar";

        // Forge 1.17+:走 @ 参数文件
        if (launchTarget.Equals("run.bat", StringComparison.OrdinalIgnoreCase))
        {
            var atArgs = ExtractAtArguments(Path.Combine(dir, "run.bat"));

            if (atArgs.Count > 0)
            {
                // 内存由 ForgeServerInstaller.WriteJvmArgs 写进 user_jvm_args.txt,
                // 这里不再重复加 -Xmx(同时出现会以后者为准,容易让用户困惑)
                args.AddRange(atArgs);
                args.Add("nogui");
                return args;
            }

            // 万一 run.bat 结构变了(没解析到 @ 引用),回退到猜测标准路径
            var fallback = FindForgeWinArgs(dir);
            if (fallback is not null)
            {
                args.Add($"-Xms{instance.MinMemoryMb}M");
                args.Add($"-Xmx{instance.MaxMemoryMb}M");
                args.Add("@" + Path.GetRelativePath(dir, fallback));
                args.Add("nogui");
                return args;
            }

            throw new InvalidOperationException(
                "无法从 run.bat 解析出 Forge 的启动参数。请尝试重新安装该服务端。");
        }

        // 内存与自定义 JVM 参数
        if (!string.IsNullOrWhiteSpace(instance.JvmArguments))
            args.AddRange(SplitArguments(instance.JvmArguments));

        args.Add($"-Xms{instance.MinMemoryMb}M");
        args.Add($"-Xmx{instance.MaxMemoryMb}M");

        // 服务端在无 GUI 环境必须加 nogui,否则原版会尝试弹窗
        args.Add("-jar");
        args.Add(launchTarget);
        args.Add("nogui");

        return args;
    }

    /// <summary>兜底:在 libraries 下找 Forge 的 win_args.txt。</summary>
    private static string? FindForgeWinArgs(string dir)
    {
        var libraries = Path.Combine(dir, "libraries");
        if (!Directory.Exists(libraries))
            return null;

        try
        {
            return Directory.GetFiles(libraries, "win_args.txt", SearchOption.AllDirectories)
                .FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
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

    /// <summary>
    /// 从 Forge 1.17+ 生成的 run.bat 里提取 @参数文件引用。
    ///
    /// run.bat 的内容形如:
    ///   @echo off
    ///   java @user_jvm_args.txt @libraries/net/minecraftforge/forge/&lt;ver&gt;/win_args.txt %*
    /// 正确启动方式是 `java @user_jvm_args.txt @libraries/.../win_args.txt nogui`,
    /// **不是** `java -jar run.bat`(那样只会报 "Invalid or corrupt jarfile")。
    ///
    /// 注意:不能简单地抓所有 `@xxx` —— batch 的 `@echo off` 也是 @ 开头,
    /// 被抓进去会让 java 报错。只认看起来像参数文件的(`.txt` 结尾或文件确实存在)。
    /// </summary>
    public static List<string> ExtractAtArguments(string runBatPath)
    {
        var result = new List<string>();
        if (!File.Exists(runBatPath))
            return result;

        string content;
        try
        {
            content = File.ReadAllText(runBatPath);
        }
        catch (IOException)
        {
            return result;
        }

        var directory = Path.GetDirectoryName(runBatPath) ?? ".";

        foreach (Match match in Regex.Matches(content, @"@([^\s""]+)"))
        {
            var raw = match.Groups[1].Value;

            // 排除 batch 指令(@echo / @rem / @set ...)
            if (!raw.Contains('.') && !raw.Contains('/') && !raw.Contains('\\'))
                continue;

            var relative = raw.Replace('/', Path.DirectorySeparatorChar);
            var full = Path.Combine(directory, relative);

            // 只保留真实存在的参数文件;不存在说明是别的 @ 用法
            if (File.Exists(full))
                result.Add("@" + relative);
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