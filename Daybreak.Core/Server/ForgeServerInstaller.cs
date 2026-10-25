using System.Diagnostics;
using System.IO;
using System.Text;
using Daybreak.Core.Game;

namespace Daybreak.Core.Server;

/// <summary>
/// Forge 服务端安装器。
///
/// 与另外三种最大的区别:Forge 没有现成的 server.jar,
/// 必须下载官方安装器并执行 `java -jar installer.jar --installServer`,
/// 由安装器内部的 processors 生成/打补丁出可运行的服务端。
///
/// 实测要点(2026-10-26):
/// - 官方源 maven.minecraftforge.net 可用(8.45MB 安装器)
/// - **BMCLAPI 镜像对该安装器返回 403**,所以这里强制走官方源,不做镜像改写
/// - 安装过程会输出大量内容,必须重定向并节流转发,否则 UI 线程会被刷爆
/// - 1.17+ 产出 run.bat / user_jvm_args.txt;1.13~1.16 产出 forge-*.jar
/// </summary>
public class ForgeServerInstaller
{
    /// <summary>Forge 安装器所在的基础地址(强制官方,原因见类注释)。</summary>
    private const string ForgeMaven = "https://maven.minecraftforge.net/net/minecraftforge/forge";

    private readonly string _mirror;

    /// <summary>安装日志回调(行)。调用方负责节流。</summary>
    public event Action<string>? OutputReceived;

    public ForgeServerInstaller(string mirror)
    {
        // Forge 安装器在 BMCLAPI 上 403,这里一律用官方源
        _mirror = mirror;
    }

    /// <summary>
    /// 安装 Forge 服务端。返回启动目标描述。
    /// </summary>
    /// <param name="gameVersion">如 1.20.1</param>
    /// <param name="forgeVersion">如 47.4.28</param>
    /// <param name="dir">服务端实例目录</param>
    /// <param name="javaPath">执行安装器用的 Java(必须与目标版本匹配)</param>
    /// <param name="progress">进度回调</param>
    public async Task<string> InstallAsync(
        string gameVersion, string forgeVersion, string dir, string javaPath,
        IProgress<LaunchProgress>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(dir);

        var fullVersion = $"{gameVersion}-{forgeVersion}";
        var installerName = $"forge-{fullVersion}-installer.jar";
        var installerUrl = $"{ForgeMaven}/{fullVersion}/{installerName}";
        var installerPath = Path.Combine(dir, installerName);

        // 1. 下载安装器(官方源,DownloadEngine 用 official 以免被改写到 403 的镜像)
        if (!File.Exists(installerPath))
        {
            progress?.Report(new LaunchProgress { CurrentFile = $"下载 Forge 安装器 {fullVersion}…" });

            using var http = ServerDownloader.CreateHttp(DownloadMirror.Official);
            var engine = new DownloadEngine(DownloadMirror.Official);
            await engine.DownloadFileAsync(http, installerUrl, installerPath, 0, null, ct);
        }

        progress?.Report(new LaunchProgress { CurrentFile = "执行 Forge 安装器(耗时较长)…" });

        // 2. 跑安装器。它自己会下载 MC 服务端与依赖、执行 processors
        var exitCode = await RunInstallerAsync(installerPath, dir, javaPath, ct);

        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Forge 安装器执行失败(退出码 {exitCode})。常见原因:Java 版本与 {gameVersion} 不匹配、" +
                "网络无法访问 Forge 的 Maven 仓库、或磁盘空间不足。详情见上方安装日志。");
        }

        // 3. 判断产出物
        var launchTarget = DetectLaunchTarget(dir, fullVersion);
        if (launchTarget is null)
        {
            throw new InvalidOperationException(
                "Forge 安装器执行完毕但没有找到可启动的服务端(run.bat / forge-*.jar)。" +
                "如果日志里有 processors 失败信息,通常是网络或 Java 版本问题。");
        }

        return launchTarget;
    }

    private async Task<int> RunInstallerAsync(
        string installerPath, string workingDir, string javaPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = javaPath,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        psi.ArgumentList.Add("-jar");
        psi.ArgumentList.Add(installerPath);
        psi.ArgumentList.Add("--installServer");

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                OutputReceived?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                OutputReceived?.Invoke(e.Data);
        };

        if (!process.Start())
            throw new InvalidOperationException("无法启动 Forge 安装器进程");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct);
        return process.ExitCode;
    }

    /// <summary>
    /// 判断安装器产出的启动方式。
    /// 1.17+ 生成 run.bat(配合 user_jvm_args.txt 控制内存);
    /// 1.13~1.16 生成 forge-&lt;版本&gt;.jar,直接 java -jar 启动。
    /// </summary>
    private static string? DetectLaunchTarget(string dir, string fullVersion)
    {
        // 优先级 1:1.17+ 的 run.bat
        var runBat = Path.Combine(dir, "run.bat");
        if (File.Exists(runBat))
            return "run.bat";

        // 优先级 2:老版本的 forge jar
        var forgeJar = Path.Combine(dir, $"forge-{fullVersion}.jar");
        if (File.Exists(forgeJar))
            return Path.GetFileName(forgeJar);

        // 优先级 3:扫描任何 forge-*.jar(版本号格式可能与预期不同)
        var candidates = Directory.GetFiles(dir, "forge-*.jar")
            .Where(f => !f.EndsWith("-installer.jar", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return candidates.Count > 0 ? Path.GetFileName(candidates[0]) : null;
    }

    /// <summary>
    /// 写 Forge 1.17+ 的内存参数文件。
    /// run.bat 会读取 user_jvm_args.txt,所以想控制内存就得写这里而不是命令行。
    /// </summary>
    public static void WriteJvmArgs(string dir, int minMemoryMb, int maxMemoryMb, string? extraArgs)
    {
        var path = Path.Combine(dir, "user_jvm_args.txt");
        var sb = new StringBuilder();

        sb.AppendLine("#由 Daybreak 破晓启动器写入:run.bat 会读取本文件作为 JVM 参数");
        sb.AppendLine("#如需自定义,请通过启动器的服务端设置修改(直接改这里会在下次开服时被覆盖)");
        sb.AppendLine($"-Xms{minMemoryMb}M");
        sb.AppendLine($"-Xmx{maxMemoryMb}M");

        if (!string.IsNullOrWhiteSpace(extraArgs))
            sb.AppendLine(extraArgs.Trim());

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }
}