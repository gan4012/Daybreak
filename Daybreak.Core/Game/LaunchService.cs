using System.Diagnostics;
using CmlLib.Core;
using CmlLib.Core.Auth;
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

/// <summary>启动请求参数(从 LauncherConfig 投影)。</summary>
public record LaunchRequest(
    string VersionId,
    string PlayerName,
    string? GameDirectory,
    string Mirror,
    int MaxMemoryMb,
    string? JavaPath);

/// <summary>
/// 启动核心门面:封装 CmlLib.Core 的版本列表 / 安装 / 启动,镜像切换经 HttpClient 改写实现。
/// </summary>
public class LaunchService
{
    private readonly string? _gameDirectory;
    private readonly string _mirror;

    public LaunchService(string? gameDirectory, string mirror)
    {
        _gameDirectory = string.IsNullOrWhiteSpace(gameDirectory) ? null : gameDirectory;
        _mirror = DownloadMirror.IsValid(mirror) ? mirror : DownloadMirror.Official;
    }

    /// <summary>游戏根目录:用户配置优先,否则系统默认 %APPDATA%/.minecraft。</summary>
    public string ResolveGameDirectory() =>
        _gameDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");

    private MinecraftLauncher CreateLauncher()
    {
        var http = new HttpClient(new MirrorHttpHandler(_mirror));
        var gamePath = new MinecraftPath(ResolveGameDirectory());
        var parameters = MinecraftLauncherParameters.CreateDefault(gamePath, http);
        return new MinecraftLauncher(parameters);
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

    public async Task InstallAsync(string versionId, IProgress<LaunchProgress>? progress)
    {
        var launcher = CreateLauncher();
        HookProgress(launcher, progress);
        await launcher.InstallAsync(versionId);
    }

    /// <summary>确保版本文件完整后启动游戏(离线会话)。</summary>
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
            Session = MSession.CreateOfflineSession(request.PlayerName),
            MaximumRamMb = request.MaxMemoryMb,
            JavaPath = string.IsNullOrWhiteSpace(request.JavaPath) ? null : request.JavaPath,
        };

        var process = await launcher.InstallAndBuildProcessAsync(request.VersionId, option);

        if (onLog is not null)
        {
            process.OutputDataReceived += (_, e) => onLog(e.Data ?? "");
            process.ErrorDataReceived += (_, e) => onLog(e.Data ?? "");
        }
        if (onExit is not null)
        {
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => onExit();
        }

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

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
        launcher.ByteProgressChanged += (_, e) => progress.Report(new LaunchProgress
        {
            ProgressedBytes = e.ProgressedBytes,
            TotalBytes = e.TotalBytes,
        });
    }
}
