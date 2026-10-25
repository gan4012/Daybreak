using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace Daybreak.Updater;

/// <summary>远程版本元数据(来自 GitHub Releases 或自定义源)。</summary>
internal record RemoteVersionInfo(string Version, string DownloadUrl, long SizeBytes);

internal static class Program
{
    private const string AppName = "Daybreak.Launcher";
    private const string ZipFileName = "Daybreak-内测包-win-x64.zip";

    /// <summary>默认更新源:本仓库的 GitHub Releases latest。</summary>
    private const string DefaultUpdateSource =
        "https://api.github.com/repos/gan4012/Daybreak/releases/latest";

    [STAThread]
    private static Task<int> Main(string[] args)
    {
        var command = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : string.Empty;

        return command switch
        {
            "--check" => CheckForUpdates(),
            "--update" => ApplyUpdate(args.Skip(1).ToArray()),
            _ => ShowHelp(),
        };
    }

    /// <summary>显示帮助信息并返回非零退出码。</summary>
    private static Task<int> ShowHelp()
    {
        Console.WriteLine("用法:");
        Console.WriteLine("  Daybreak.Updater.exe --check       检查是否有新版本可用");
        Console.WriteLine("  Daybreak.Updater.exe --update      下载并应用最新更新(等待主程序退出后覆盖+重启)");
        Console.WriteLine();
        Console.WriteLine("说明:");
        Console.WriteLine("  --check  读取 config.json 的 UpdateSourceUrl 作为更新源,未配置时用本仓库 GitHub Releases。");
        Console.WriteLine("           若有新版本则输出 \"NEW:<version>:<url>\" 到 stdout,否则输出 \"UP_TO_DATE\"。");
        Console.WriteLine("  --update 等待主程序退出,然后下载新版本 zip 并解压覆盖当前目录,最后重启主程序。");
        return Task.FromResult(1);
    }

    /// <summary>
    /// 从 config.json 读取 UpdateSourceUrl。
    /// 刻意不引用 Daybreak.Core:更新器必须在裁剪(self-contained + trim)下仍可用,
    /// 且要能在主程序文件被占用的场景下独立运行,因此只用 JsonDocument 手工取值。
    /// </summary>
    private static string? ReadConfiguredUpdateSource()
    {
        try
        {
            var configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Daybreak", "config.json");
            if (!File.Exists(configPath))
                return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("UpdateSourceUrl", out var url) &&
                url.ValueKind == JsonValueKind.String)
            {
                var value = url.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
        catch
        {
            // 配置缺失/损坏都回退默认源
        }
        return null;
    }

    /// <summary>检查更新:比较当前版本与远程最新版本。</summary>
    private static async Task<int> CheckForUpdates()
    {
        try
        {
            var currentVersion = GetLauncherVersion();
            var remoteUrl = ResolveRemoteUrl();

            Console.WriteLine($"[Updater] 当前版本: {currentVersion}");
            Console.WriteLine($"[Updater] 更新源: {remoteUrl}");

            var remote = await FetchRemoteVersion(remoteUrl);
            if (remote is null)
            {
                Console.WriteLine("[Updater] 无法获取远程版本信息,跳过检查");
                return 0;
            }

            Console.WriteLine($"[Updater] 远程版本: {remote.Version}");

            if (CompareVersions(currentVersion, remote.Version) >= 0)
            {
                Console.WriteLine("UP_TO_DATE");
                return 0;
            }

            // 有新版本:输出结构化消息供主程序解析
            Console.WriteLine($"NEW:{remote.Version}:{remote.DownloadUrl}");
            return 2; // 特殊退出码表示有更新
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Updater] 检查更新失败: {ex.Message}");
            return 1;
        }
    }

    /// <summary>应用更新:下载 zip、等待主程序退出、解压覆盖、重启。</summary>
    private static async Task<int> ApplyUpdate(string[] extraArgs)
    {
        try
        {
            var downloadUrl = extraArgs.Length > 0 ? extraArgs[0] : null;
            if (string.IsNullOrEmpty(downloadUrl))
            {
                // 尝试从远程获取
                var remoteUrl = ResolveRemoteUrl();
                var remote = await FetchRemoteVersion(remoteUrl);
                if (remote is null)
                {
                    Console.WriteLine("[Updater] 无可用更新");
                    return 0;
                }
                downloadUrl = remote.DownloadUrl;
            }

            Console.WriteLine($"[Updater] 开始下载更新: {downloadUrl}");

            var tempDir = Path.Combine(Path.GetTempPath(), "Daybreak", "update");
            Directory.CreateDirectory(tempDir);
            var zipPath = Path.Combine(tempDir, ZipFileName);

            using var http = new HttpClient(new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            })
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Daybreak-Launcher/1.0 (Minecraft launcher)");

            // 先下载 zip
            Console.WriteLine("[Updater] 下载中...");
            var data = await http.GetByteArrayAsync(downloadUrl);
            await File.WriteAllBytesAsync(zipPath, data);
            Console.WriteLine($"[Updater] 下载完成: {data.Length / 1024 / 1024:F1} MB");

            // 等待主程序退出
            Console.WriteLine("[Updater] 等待主程序退出...");
            WaitForLauncherExit();

            // 解压覆盖
            Console.WriteLine("[Updater] 解压覆盖中...");
            var exeDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName!)
                         ?? Environment.CurrentDirectory;
            CleanupStaleFiles(exeDir);
            ExtractAndReplace(zipPath, exeDir);

            // 重启主程序
            var launcherExe = Path.Combine(exeDir, AppName + ".exe");
            if (File.Exists(launcherExe))
            {
                Console.WriteLine("[Updater] 重启主程序...");
                Process.Start(new ProcessStartInfo
                {
                    FileName = launcherExe,
                    UseShellExecute = true,
                });
            }
            else
            {
                Console.WriteLine("[Updater] 警告:未找到主程序 exe,请手动启动");
            }

            // 清理临时文件
            try { File.Delete(zipPath); } catch { /* ignore */ }

            Console.WriteLine("[Updater] 更新完成!");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Updater] 应用更新失败: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// 获取**主程序**版本号用于更新比较。
    /// 注意不能用自己的版本:更新器与主程序版本号是两套,自己比较会得出错误结论。
    /// 读同目录 Daybreak.Launcher.exe 的 FileVersion(形如 0.1.0.0,归一化为 0.1.0)。
    /// </summary>
    private static string GetLauncherVersion()
    {
        try
        {
            var exeDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName!)
                         ?? Environment.CurrentDirectory;
            var launcherExe = Path.Combine(exeDir, AppName + ".exe");
            if (File.Exists(launcherExe))
            {
                var fileVersion = FileVersionInfo.GetVersionInfo(launcherExe).FileVersion;
                if (!string.IsNullOrWhiteSpace(fileVersion))
                    return NormalizeVersion(fileVersion);
            }
        }
        catch
        {
            // 读不到就回退自身版本(仅影响提示文案)
        }

        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        return NormalizeVersion(info?.InformationalVersion ?? asm.GetName().Version?.ToString() ?? "0.0.0");
    }

    /// <summary>归一化版本号:去掉 +commit 后缀,并统一为前三段(0.1.0.0 → 0.1.0)。</summary>
    private static string NormalizeVersion(string version)
    {
        var plus = version.IndexOf('+');
        if (plus >= 0)
            version = version[..plus];

        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 3 ? string.Join('.', parts) : string.Join('.', parts.Take(3));
    }

    /// <summary>解析更新源 URL:优先使用 config.json 里的自定义地址,否则用本仓库 GitHub Releases。</summary>
    private static string ResolveRemoteUrl()
    {
        var configured = ReadConfiguredUpdateSource();
        return string.IsNullOrWhiteSpace(configured) ? DefaultUpdateSource : configured;
    }

    /// <summary>从远程 URL 获取最新版本信息。</summary>
    private static async Task<RemoteVersionInfo?> FetchRemoteVersion(string remoteUrl)
    {
        using var http = new HttpClient()
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Daybreak-Launcher/1.0");

        try
        {
            var json = await http.GetStringAsync(remoteUrl);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // GitHub Releases API 响应
            var tagName = root.TryGetProperty("tag_name", out var tn) ? tn.GetString() : null;
            JsonDocument? assetsDoc = null;
            if (root.TryGetProperty("assets", out var assetsEl))
                assetsDoc = JsonDocument.Parse(assetsEl.GetRawText());

            if (tagName is not null && assetsDoc is not null)
            {
                // 查找匹配的 zip 资产
                foreach (var asset in assetsDoc.RootElement.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString();
                    if (name == ZipFileName)
                    {
                        var browserDownloadUrl = asset.GetProperty("browser_download_url").GetString();
                        return new RemoteVersionInfo(tagName, browserDownloadUrl!, asset.GetProperty("size").GetInt64());
                    }
                }
            }

            // 如果不是 GitHub 格式,尝试自定义 JSON 格式:{"version":"x.y.z","download_url":"..."}
            var customVersion = root.TryGetProperty("version", out var v) ? v.GetString() : null;
            var customUrl = root.TryGetProperty("download_url", out var u) ? u.GetString() : null;
            if (customVersion is not null && customUrl is not null)
            {
                return new RemoteVersionInfo(customVersion, customUrl, 0);
            }
        }
        catch
        {
            // 网络错误或 JSON 解析失败
        }

        return null;
    }

    /// <summary>简单语义版本比较:返回 >0 表示 a>b,==0 相等,<0 表示 a<b。</summary>
    private static int CompareVersions(string a, string b)
    {
        // 去掉前导 v
        a = a.TrimStart('v');
        b = b.TrimStart('v');

        var partsA = a.Split('.');
        var partsB = b.Split('.');
        var maxLen = Math.Max(partsA.Length, partsB.Length);

        for (var i = 0; i < maxLen; i++)
        {
            var pa = i < partsA.Length ? partsA[i] : "0";
            var pb = i < partsB.Length ? partsB[i] : "0";

            // 尝试数字比较
            if (int.TryParse(pa, out var na) && int.TryParse(pb, out var nb))
            {
                var cmp = na.CompareTo(nb);
                if (cmp != 0) return cmp;
            }
            else
            {
                // 字符串比较(如 beta/rc)
                var cmp = string.Compare(pa, pb, StringComparison.Ordinal);
                if (cmp != 0) return cmp;
            }
        }

        return 0;
    }

    /// <summary>等待主程序退出(最多 30 秒)。</summary>
    private static void WaitForLauncherExit()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var processes = Process.GetProcessesByName(AppName);
            if (processes.Length == 0)
                break;
            Thread.Sleep(500);
        }
    }

    /// <summary>
    /// 解压 zip 到目标目录,已有文件覆盖。
    /// 特例:更新器自身正在运行,不能直接覆盖——先重命名为 .old(Windows 允许重命名运行中的 exe),
    /// 再写入新文件;下次启动时清理 .old。
    /// </summary>
    private static void ExtractAndReplace(string zipPath, string targetDir)
    {
        var selfPath = Process.GetCurrentProcess().MainModule?.FileName;

        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            // 跳过目录条目本身
            if (entry.Name == string.Empty)
                continue;

            var relativePath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            var targetPath = Path.Combine(targetDir, relativePath);

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            if (selfPath is not null &&
                string.Equals(Path.GetFullPath(targetPath), Path.GetFullPath(selfPath), StringComparison.OrdinalIgnoreCase))
            {
                // 正在运行的自身 exe:重命名腾位
                var parked = selfPath + ".old";
                try
                {
                    if (File.Exists(parked)) File.Delete(parked);
                    File.Move(selfPath, parked);
                    entry.ExtractToFile(targetPath, overwrite: true);
                    Console.WriteLine("[Updater] 已替换更新器自身(旧文件将在下次启动时清理)");
                }
                catch (Exception ex)
                {
                    // 替换失败不影响主程序更新,下次更新时会再试
                    Console.Error.WriteLine($"[Updater] 更新器自身替换失败(可忽略):{ex.Message}");
                    try { entry.ExtractToFile(targetPath, overwrite: false); } catch { /* 保留旧文件 */ }
                }
                continue;
            }

            entry.ExtractToFile(targetPath, overwrite: true);
        }
    }

    /// <summary>清理上次更新残留的 .old 文件(此时旧的更新器已不再运行)。</summary>
    private static void CleanupStaleFiles(string dir)
    {
        try
        {
            foreach (var stale in Directory.EnumerateFiles(dir, "*.old", SearchOption.AllDirectories))
            {
                try { File.Delete(stale); }
                catch { /* 仍被占用则下次再清 */ }
            }
        }
        catch { /* 清理失败不影响更新 */ }
    }
}
