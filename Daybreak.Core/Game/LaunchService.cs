using System.Text.RegularExpressions;
using System.Diagnostics;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.CommandParser;
using CmlLib.Core.Installers;
using CmlLib.Core.ProcessBuilder;
using CmlLib.Core.Version;
using CmlLib.Core.VersionMetadata;

namespace Daybreak.Core.Game;

/// <summary>安装/启动进度快照(UI 线程自行调度)。</summary>
public class LaunchProgress
{
    public string? CurrentFile { get; init; }
    public int ProgressedTasks { get; init; }
    public int TotalTasks { get; init; }
    public long ProgressedBytes { get; init; }
    public long TotalBytes { get; init; }

    /// <summary>0~100;TotalTasks 未知时为 0(调用方按不确定进度显示)。</summary>
    public int Percent =>
        TotalTasks <= 0 ? 0 : (int)((double)ProgressedTasks / TotalTasks * 100);
}

/// <summary>启动请求参数(从 LauncherConfig 与登录结果投影)。</summary>
public record LaunchRequest(
    string VersionId,
    string PlayerName,
    string? GameDirectory,
    string Mirror,
    int MaxMemoryMb,
    string? JavaPath)
{
    /// <summary>已认证会话;null 时按离线模式使用 PlayerName。</summary>
    public MSession? Session { get; init; }

    /// <summary>外置登录:authlib-injector.jar 的完整路径(与 AuthServerUrl 一起注入)。</summary>
    public string? JavaAgentJarPath { get; init; }

    /// <summary>外置登录:Yggdrasil API 根地址。</summary>
    public string? AuthServerUrl { get; init; }

    /// <summary>自定义 JVM 参数(空格分隔,如 -XX:+UseG1GC)。</summary>
    public string? JvmArguments { get; init; }

    /// <summary>
    /// 追加到游戏主类的参数(如联机直连的 --server / --port)。
    /// 与 JVM 参数不同,这些是传给 Minecraft 本体而不是 Java 的。
    /// </summary>
    public IReadOnlyList<string>? ExtraGameArguments { get; init; }
}

/// <summary>加载器类型。</summary>
public static class ModLoader
{
    public const string Vanilla = "vanilla";
    public const string Fabric = "fabric";
    public const string Forge = "forge";
    public const string NeoForge = "neoforge";
}

/// <summary>
/// 启动核心门面:封装 CmlLib.Core 的版本列表 / 安装 / 启动,镜像切换经 HttpClient 改写实现。
/// </summary>
public class LaunchService
{
    private readonly string? _gameDirectory;
    private readonly string _mirror;
    private readonly bool _versionIsolation;

    public LaunchService(string? gameDirectory, string mirror, bool versionIsolation = false)
    {
        _gameDirectory = string.IsNullOrWhiteSpace(gameDirectory) ? null : gameDirectory;
        _mirror = DownloadMirror.IsValid(mirror) ? mirror : DownloadMirror.Official;
        _versionIsolation = versionIsolation;
    }

    /// <summary>游戏根目录:用户配置优先,否则系统默认 %APPDATA%/.minecraft。</summary>
    public string ResolveGameDirectory() =>
        _gameDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");

    /// <summary>构建带镜像/重试处理的启动器(供本服务与 LoaderService 共用);下载并发高于默认值。</summary>
    public static MinecraftLauncher CreateLauncher(string? gameDirectory, string mirror)
    {
        // 超时必须为无限:大资源(数百 MB)的响应体读取可能超过 HttpClient 默认 100s,
        // 被掐断后安装器会静默挂起;整体节奏由 UI 进度呈现
        var http = new HttpClient(new ResilientHttpHandler(mirror))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var resolved = string.IsNullOrWhiteSpace(gameDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft")
            : gameDirectory;
        var parameters = MinecraftLauncherParameters.CreateDefault(new MinecraftPath(resolved), http);
        var cores = Environment.ProcessorCount;
        parameters.GameInstaller = new ParallelGameInstaller(
            Math.Max(4, cores), Math.Max(8, cores * 2), 2048, http);
        return new MinecraftLauncher(parameters);
    }

    private MinecraftLauncher CreateLauncher() => CreateLauncher(_gameDirectory, _mirror);

    /// <summary>默认共享游戏目录的 MinecraftPath。</summary>
    public MinecraftPath CreateGamePath() =>
        new MinecraftPath(ResolveGameDirectory());

    /// <summary>某版本的 mods 目录(遵循版本隔离:隔离时位于版本文件夹内)。</summary>
    public string ResolveModsDirectory(string versionId)
    {
        var path = ResolvePathForVersion(versionId);
        return Path.Combine(path.BasePath, "mods");
    }

    /// <summary>某版本的游戏运行根目录(遵循版本隔离)。</summary>
    public string ResolveGameRootForVersion(string versionId) =>
        ResolvePathForVersion(versionId).BasePath;

    /// <summary>
    /// 解析某版本的 MinecraftPath。开启版本隔离时:游戏运行目录(saves/config 等)在版本文件夹内,
    /// 而 versions/libraries/assets/runtime 仍指向共享位置,避免重复下载。
    /// </summary>
    private MinecraftPath ResolvePathForVersion(string versionId)
    {
        var shared = CreateGamePath();
        if (!_versionIsolation)
            return shared;

        var isolatedBase = Path.Combine(shared.BasePath, "versions", versionId);
        var isolated = new MinecraftPath(isolatedBase)
        {
            Versions = shared.Versions,
            Library = shared.Library,
            Assets = shared.Assets,
            Runtime = shared.Runtime,
        };
        return isolated;
    }

    /// <summary>解析版本 JSON 里的 Java 运行时组件名(优先 component 字段,缺省按大版本映射)。</summary>
    public async Task<string?> GetJavaComponentAsync(string versionId)
    {
        var launcher = CreateLauncher();
        var version = await launcher.GetVersionAsync(versionId);
        var javaVersion = version.GetInheritedProperty(v => v.JavaVersion);
        if (javaVersion is null)
            return null;

        if (!string.IsNullOrEmpty(javaVersion.Component))
            return javaVersion.Component;

        return int.TryParse(javaVersion.MajorVersion, out var major)
            ? MojangJavaInstaller.ResolveComponent(major)
            : null;
    }

    /// <summary>
    /// 确保有"版本兼容"的 Java。优先级:
    /// 1) 版本 JSON 声明了 javaVersion 组件 → 使用/下载官方运行时(保证 JVM 参数兼容);
    /// 2) 无组件信息 → 探测系统 Java 并按大版本校验,不满足时下载匹配运行时。
    /// </summary>
    public async Task<string?> EnsureJavaAsync(
        string versionId, string? configuredJavaDirectory, IProgress<LaunchProgress>? progress)
    {
        var component = await GetJavaComponentAsync(versionId);
        if (component is not null)
        {
            // 版本声明了组件就认官方运行时:新版本可能带老 JVM 不认识的参数
            var runtimeRoot = Path.Combine(CreateGamePath().Runtime, "windows-x64");
            var runtimeDir = await new MojangJavaInstaller(_mirror)
                .InstallAsync(runtimeRoot, component, progress);
            return Path.Combine(runtimeDir, "bin", "java.exe");
        }

        var requiredMajor = await GetJavaMajorVersionAsync(versionId);
        var found = JavaLocator.FindJava(configuredJavaDirectory);
        if (found is not null)
        {
            var systemMajor = GetJavaMajor(found);
            if (systemMajor is null || requiredMajor is null || systemMajor >= requiredMajor)
                return found;

            progress?.Report(new LaunchProgress
            {
                CurrentFile = $"系统 Java({systemMajor})低于该版本要求的 Java {requiredMajor},准备官方运行时",
            });
        }

        var fallbackComponent = requiredMajor is not null
            ? MojangJavaInstaller.ResolveComponent(requiredMajor)
            : MojangJavaInstaller.DefaultComponent;

        var fallbackRoot = Path.Combine(CreateGamePath().Runtime, "windows-x64");
        var fallbackDir = await new MojangJavaInstaller(_mirror)
            .InstallAsync(fallbackRoot, fallbackComponent, progress);
        return Path.Combine(fallbackDir, "bin", "java.exe");
    }

    /// <summary>版本要求的 Java 主版本(取自版本 JSON 的 javaVersion.majorVersion)。</summary>
    public async Task<int?> GetJavaMajorVersionAsync(string versionId)
    {
        var launcher = CreateLauncher();
        var version = await launcher.GetVersionAsync(versionId);
        var javaVersion = version.GetInheritedProperty(v => v.JavaVersion);
        return int.TryParse(javaVersion?.MajorVersion, out var major) ? major : null;
    }

    /// <summary>运行 java -version 探测主版本;失败返回 null(未知则不拦截)。</summary>
    private static int? GetJavaMajor(string javaExe)
    {
        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = javaExe,
                    Arguments = "-version",
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            process.Start();
            var output = process.StandardError.ReadToEnd();
            process.WaitForExit(10_000);

            var match = Regex.Match(output, @"version ""(?<v>\d+)(?:\.(?<minor>\d+))?");
            if (!match.Success)
                return null;

            var major = int.Parse(match.Groups["v"].Value);
            if (major == 1 && match.Groups["minor"].Success)
                major = int.Parse(match.Groups["minor"].Value); // 1.8 → 8
            return major;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>官方清单全部版本(含快照与历史版本),清单自带最新在前。</summary>
    public async Task<IReadOnlyList<IVersionMetadata>> GetOnlineVersionsAsync()
    {
        var launcher = CreateLauncher();
        var collection = await launcher.GetAllVersionsAsync();
        var list = new List<IVersionMetadata>();
        foreach (var metadata in collection)
            list.Add(metadata);
        return list;
    }

    /// <summary>本地已安装版本目录名(存在同名 .json 才算完整)。</summary>
    public IReadOnlyList<string> GetInstalledVersions()
    {
        return GetInstalledVersionInfos().Select(i => i.Name).ToList();
    }

    /// <summary>本地已安装版本(含磁盘占用),按名称倒序。</summary>
    public IReadOnlyList<InstalledVersionInfo> GetInstalledVersionInfos()
    {
        var versionsDir = Path.Combine(ResolveGameDirectory(), "versions");
        if (!Directory.Exists(versionsDir))
            return [];

        var list = new List<InstalledVersionInfo>();
        foreach (var dir in Directory.EnumerateDirectories(versionsDir))
        {
            var name = Path.GetFileName(dir);
            if (name is null || !File.Exists(Path.Combine(dir, name + ".json")))
                continue;

            long size = 0;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { size += new FileInfo(file).Length; }
                catch (IOException) { /* 文件被占用时跳过该项 */ }
            }

            list.Add(new InstalledVersionInfo(name, size));
        }

        return list.OrderByDescending(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// 判断某版本是否**真正装完**(不只是目录存在)。
    ///
    /// 为什么需要它:安装过程中 CmlLib 会**很早就写入版本 JSON**,
    /// 所以"目录里有 json"不代表装好了——此刻资源/库可能还在下。
    /// 若用它判断"已安装",列表会一边显示「已安装」一边还在下载(实测踩过)。
    ///
    /// 检查项:
    /// 1. 版本 JSON 存在
    /// 2. 客户端 jar 存在(加载器版本可继承父版本,沿 inheritsFrom 链找)
    /// 3. **没有 .part 残留**(有残留说明上次下载没完成)
    /// </summary>
    public bool IsVersionComplete(string versionName, int depth = 0)
    {
        if (depth > 5 || string.IsNullOrWhiteSpace(versionName))
            return false;

        var versionsDir = Path.Combine(ResolveGameDirectory(), "versions");
        var dir = Path.Combine(versionsDir, versionName);
        var jsonPath = Path.Combine(dir, versionName + ".json");

        if (!File.Exists(jsonPath))
            return false;

        // 有 .part 说明上次下载中断,不视为完整
        try
        {
            if (Directory.EnumerateFiles(dir, "*.part", SearchOption.TopDirectoryOnly).Any())
                return false;
        }
        catch (IOException)
        {
            // 读不了目录就别卡在这,继续后面的检查
        }

        string? inheritsFrom = null;
        string? jarField = null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(jsonPath));
            if (doc.RootElement.TryGetProperty("inheritsFrom", out var inh) &&
                inh.ValueKind == System.Text.Json.JsonValueKind.String)
                inheritsFrom = inh.GetString();
            if (doc.RootElement.TryGetProperty("jar", out var jar) &&
                jar.ValueKind == System.Text.Json.JsonValueKind.String)
                jarField = jar.GetString();
        }
        catch (Exception)
        {
            return false;
        }

        // 本目录有 jar 就算齐
        var candidate = Path.Combine(dir, (jarField ?? versionName) + ".jar");
        if (File.Exists(candidate) || File.Exists(Path.Combine(dir, versionName + ".jar")))
            return true;

        // 否则沿继承链检查父版本(加载器版本是这种形态)
        return inheritsFrom is not null && IsVersionComplete(inheritsFrom, depth + 1);
    }

    /// <summary>版本内的 Mod 文件清单(仅 *.jar)。</summary>
    public IReadOnlyList<InstalledVersionInfo> GetMods(string versionId)
    {
        var modsDir = ResolveModsDirectory(versionId);
        if (!Directory.Exists(modsDir))
            return [];

        return Directory.EnumerateFiles(modsDir, "*.jar")
            .Select(f => new InstalledVersionInfo(Path.GetFileName(f), new FileInfo(f).Length))
            .OrderByDescending(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>删除版本:移除版本目录(json/jar)及其隔离游戏目录(若存在)。不可恢复。</summary>
    public void DeleteVersion(string versionId)
    {
        var shared = CreateGamePath();
        var versionDir = Path.Combine(shared.Versions, versionId);
        if (Directory.Exists(versionDir))
            Directory.Delete(versionDir, recursive: true);

        // 隔离目录与版本目录是同一个位置;非隔离时若存在历史隔离目录也一并清理
        var isolated = Path.Combine(shared.BasePath, "versions", versionId);
        if (!string.Equals(isolated, versionDir, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(isolated))
            Directory.Delete(isolated, recursive: true);
    }

    /// <summary>重命名版本:修改目录名、json 文件名、以及 json 内的 id/jar 字段引用。</summary>
    public void RenameVersion(string oldName, string newName)
    {
        var shared = CreateGamePath();
        var oldDir = Path.Combine(shared.Versions, oldName);
        var newDir = Path.Combine(shared.Versions, newName);

        if (!Directory.Exists(oldDir))
            throw new InvalidOperationException($"版本目录不存在: {oldName}");

        if (Directory.Exists(newDir))
            throw new InvalidOperationException($"目标版本已存在: {newName}");

        Directory.Move(oldDir, newDir);

        // 更新 json 内的 id 和 jar 字段
        var jsonPath = Path.Combine(newDir, oldName + ".json");
        var newPath = Path.Combine(newDir, newName + ".json");
        if (File.Exists(jsonPath))
        {
            var jsonText = File.ReadAllText(jsonPath);
            using var doc = System.Text.Json.JsonDocument.Parse(jsonText);
            var options = new System.IO.MemoryStream();
            using var writer = new System.Text.Json.Utf8JsonWriter(options, new System.Text.Json.JsonWriterOptions { Indented = true });
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name == "id")
                    writer.WriteString("id", newName);
                else if (prop.Name == "jar" && prop.Value.GetString() == oldName)
                    writer.WriteString("jar", newName);
                else
                    prop.WriteTo(writer);
            }
            writer.Flush();
            File.WriteAllText(newPath, System.Text.Encoding.UTF8.GetString(options.ToArray()));
            File.Delete(jsonPath);
        }
    }

    /// <summary>复制版本:全量复制版本目录到新名字,重写 json id。</summary>
    public void CopyVersion(string sourceName, string targetName)
    {
        var shared = CreateGamePath();
        var srcDir = Path.Combine(shared.Versions, sourceName);
        var dstDir = Path.Combine(shared.Versions, targetName);

        if (!Directory.Exists(srcDir))
            throw new InvalidOperationException($"源版本不存在: {sourceName}");

        if (Directory.Exists(dstDir))
            throw new InvalidOperationException($"目标版本已存在: {targetName}");

        // 递归复制目录
        CopyDirectory(srcDir, dstDir);

        // 重写 json 中的 id 和 jar
        var srcJson = Path.Combine(srcDir, sourceName + ".json");
        var dstJson = Path.Combine(dstDir, targetName + ".json");
        if (File.Exists(srcJson))
        {
            var jsonText = File.ReadAllText(srcJson);
            using var doc = System.Text.Json.JsonDocument.Parse(jsonText);
            var options = new System.IO.MemoryStream();
            using var writer = new System.Text.Json.Utf8JsonWriter(options, new System.Text.Json.JsonWriterOptions { Indented = true });
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name == "id")
                    writer.WriteString("id", targetName);
                else if (prop.Name == "jar" && prop.Value.GetString() == sourceName)
                    writer.WriteString("jar", targetName);
                else
                    prop.WriteTo(writer);
            }
            writer.Flush();
            File.WriteAllText(dstJson, System.Text.Encoding.UTF8.GetString(options.ToArray()));
            File.Delete(srcJson);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var destFile = Path.Combine(destination, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: false);
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            var destDir = Path.Combine(destination, Path.GetFileName(dir));
            CopyDirectory(dir, destDir);
        }
    }

    public async Task InstallAsync(string versionId, IProgress<LaunchProgress>? progress)
    {
        var launcher = CreateLauncher();
        HookProgress(launcher, progress);
        await launcher.InstallAsync(versionId);
    }

    /// <summary>安装时同样遵循版本隔离(把该版本的游戏文件装进隔离目录)。</summary>
    public async Task InstallAsync(string versionId, bool isolate, IProgress<LaunchProgress>? progress)
    {
        var path = isolate ? ResolvePathForVersion(versionId) : CreateGamePath();
        var http = new HttpClient(new ResilientHttpHandler(_mirror));
        var parameters = MinecraftLauncherParameters.CreateDefault(path, http);
        var launcher = new MinecraftLauncher(parameters);
        HookProgress(launcher, progress);
        await launcher.InstallAsync(versionId);
    }

    /// <summary>确保版本文件完整后启动游戏(离线 / 正版 / 外置会话)。</summary>
    public async Task<Process> LaunchAsync(
        LaunchRequest request,
        IProgress<LaunchProgress>? progress,
        Action<string>? onLog,
        Action? onExit)
    {
        var launcher = CreateLauncher();
        HookProgress(launcher, progress);

        var option = new MLaunchOption
        {
            Session = request.Session ?? MSession.CreateOfflineSession(request.PlayerName),
            MaximumRamMb = request.MaxMemoryMb,
            JavaPath = string.IsNullOrWhiteSpace(request.JavaPath) ? null : request.JavaPath,
            Path = ResolvePathForVersion(request.VersionId),
        };

        if (!string.IsNullOrWhiteSpace(request.JavaAgentJarPath) &&
            !string.IsNullOrWhiteSpace(request.AuthServerUrl))
        {
            // authlib-injector:以 javaagent 方式注入外置登录
            var agentArgument = new MArgument($"-javaagent:{request.JavaAgentJarPath}={request.AuthServerUrl}");
            option.ExtraJvmArguments = option.ExtraJvmArguments.Append(agentArgument);
        }

        // 追加用户自定义的 JVM 参数
        if (!string.IsNullOrWhiteSpace(request.JvmArguments))
        {
            foreach (var arg in request.JvmArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                option.ExtraJvmArguments = option.ExtraJvmArguments.Append(new MArgument(arg));
            }
        }

        // 追加游戏本体参数(联机直连的 --server/--port 走这里)
        if (request.ExtraGameArguments is { Count: > 0 })
        {
            foreach (var arg in request.ExtraGameArguments)
            {
                option.ExtraGameArguments = option.ExtraGameArguments.Append(new MArgument(arg));
            }
        }

        var process = await launcher.InstallAndBuildProcessAsync(request.VersionId, option);

        // 诊断:把实际使用的 Java 路径写进日志(可确认运行时是否匹配)
        onLog?.Invoke($"[诊断] 实际 Java:{process.StartInfo.FileName}");
        onLog?.Invoke($"[诊断] JVM 参数:{Truncate(process.StartInfo.Arguments, 300)}");

        // 强制重定向:CmlLib 构建的 Process 默认不重定向输出,游戏秒退时无从诊断
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;

        if (onExit is not null)
        {
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => onExit();
        }

        process.Start();

        if (onLog is not null)
        {
            // 行式异步读取;进程秒退时流关闭、循环自然结束
            _ = Task.Run(async () =>
            {
                try
                {
                    while (await process.StandardOutput.ReadLineAsync() is { } line)
                        onLog(line);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
            });
            _ = Task.Run(async () =>
            {
                try
                {
                    while (await process.StandardError.ReadLineAsync() is { } line)
                        onLog(line);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException) { }
            });
        }

        return process;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private static void HookProgress(MinecraftLauncher launcher, IProgress<LaunchProgress>? progress)
    {
        if (progress is null)
            return;

        launcher.FileProgressChanged += (_, e) => progress.Report(new LaunchProgress
        {
            CurrentFile = e.Name,
            ProgressedTasks = e.ProgressedTasks,
            TotalTasks = e.TotalTasks,
        });

        // 字节进度节流:每 1MB 才上报一次(原始事件每秒可达数千次,
        // 全量上报会淹没 UI 线程的调度队列)
        long lastReportedBytes = 0;
        launcher.ByteProgressChanged += (_, e) =>
        {
            if (e.TotalBytes > 0 &&
                e.ProgressedBytes != e.TotalBytes &&
                e.ProgressedBytes - lastReportedBytes < 1_048_576)
                return;
            lastReportedBytes = e.ProgressedBytes;
            progress.Report(new LaunchProgress
            {
                ProgressedBytes = e.ProgressedBytes,
                TotalBytes = e.TotalBytes,
            });
        };
    }
}
