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

    /// <summary>
    /// 发布包文件名(必须全 ASCII:GitHub Release 上传会把非 ASCII 字符替换成 '.')。
    /// 匹配时优先精确名,失败则退回「以 -win-x64.zip 结尾」的宽松规则,避免因历史改名失效。
    /// </summary>
    private const string ZipFileName = "Daybreak-beta-win-x64.zip";
    private const string ZipNameSuffix = "-win-x64.zip";

    /// <summary>
    /// 默认更新源,按顺序尝试:先 Gitee(国内直连快),失败再回退 GitHub(海外/有代理时)。
    /// 两个仓库地址不同(GitHub 是 gan4012/Daybreak,Gitee 是 wergansailikebai/daybreak),故分开常量。
    /// </summary>
    private static readonly string[] DefaultUpdateSources =
    [
        "https://gitee.com/api/v5/repos/wergansailikebai/daybreak/releases/latest",
        "https://api.github.com/repos/gan4012/Daybreak/releases/latest",
    ];

    [STAThread]
    private static Task<int> Main(string[] args)
    {
        // 任何命令都先清理上次更新留下的 .old 文件。
        // 更新器替换自身时会把运行中的 exe 重命名为 .old(Windows 不允许直接覆盖运行中的文件),
        // 若只在 --update 里清理,用户跑完更新后再不更新就会一直留着这个 11MB 孤儿文件。
        CleanupStaleFilesNextToSelf();

        var command = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : string.Empty;

        return command switch
        {
            "--check" => CheckForUpdates(),
            "--update" => ApplyUpdate(args.Skip(1).ToArray()),
            _ => ShowHelp(),
        };
    }

    /// <summary>清理更新器所在目录里的 .old 残留(失败不影响命令执行)。</summary>
    private static void CleanupStaleFilesNextToSelf()
    {
        try
        {
            var exeDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule?.FileName!);
            if (!string.IsNullOrEmpty(exeDir))
                CleanupStaleFiles(exeDir);
        }
        catch
        {
            // 拿不到路径就跳过,不影响主流程
        }
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
            Console.WriteLine($"[Updater] 当前版本: {currentVersion}");

            var remote = await FetchRemoteFromAnySourceAsync();
            if (remote is null)
            {
                Console.WriteLine("[Updater] 所有更新源均无法获取版本信息,跳过检查");
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

    /// <summary>按顺序尝试各更新源,返回第一个能给出可用版本信息的源。</summary>
    private static async Task<RemoteVersionInfo?> FetchRemoteFromAnySourceAsync()
    {
        foreach (var source in ResolveUpdateSources())
        {
            Console.WriteLine($"[Updater] 查询更新源:{source}");
            var remote = await FetchRemoteVersion(source);
            if (remote is not null)
                return remote;

            Console.WriteLine("[Updater] 该源无可用信息,尝试下一个");
        }
        return null;
    }

    /// <summary>应用更新:下载 zip、等待主程序退出、解压覆盖、重启。</summary>
    private static async Task<int> ApplyUpdate(string[] extraArgs)
    {
        try
        {
            var downloadUrl = extraArgs.Length > 0 ? extraArgs[0] : null;
            if (string.IsNullOrEmpty(downloadUrl))
            {
                // 未显式给 URL:从更新源查最新版
                var remote = await FetchRemoteFromAnySourceAsync();
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

            await DownloadWithFallbackAsync(downloadUrl, zipPath);

            // 覆盖前先校验压缩包:内容不完整就中止,避免把安装目录写成半残状态
            ValidatePackage(zipPath);

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

    /// <summary>
    /// 返回要尝试的更新源列表:config.json 设了 UpdateSourceUrl 就只用它(用户强制指定),
    /// 否则用默认的 Gitee → GitHub 双源。
    /// </summary>
    private static IReadOnlyList<string> ResolveUpdateSources()
    {
        var configured = ReadConfiguredUpdateSource();
        return string.IsNullOrWhiteSpace(configured)
            ? DefaultUpdateSources
            : [configured];
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
                // 先找精确名;没有则退回后缀匹配(兼容历史改名/中文名被 GitHub 改写的情况)
                string? fallbackName = null;
                string? fallbackUrl = null;
                long fallbackSize = 0;

                foreach (var asset in assetsDoc.RootElement.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString();
                    if (string.IsNullOrEmpty(name))
                        continue;

                    var assetUrl = asset.GetProperty("browser_download_url").GetString();
                    var assetSize = asset.TryGetProperty("size", out var sizeEl) ? sizeEl.GetInt64() : 0;

                    if (name == ZipFileName && assetUrl is not null)
                        return new RemoteVersionInfo(tagName, assetUrl, assetSize);

                    if (fallbackName is null &&
                        name.EndsWith(ZipNameSuffix, StringComparison.OrdinalIgnoreCase) &&
                        assetUrl is not null)
                    {
                        fallbackName = name;
                        fallbackUrl = assetUrl;
                        fallbackSize = assetSize;
                    }
                }

                if (fallbackUrl is not null)
                {
                    Console.WriteLine($"[Updater] 未找到标准包名 {ZipFileName},使用同后缀资产:{fallbackName}");
                    return new RemoteVersionInfo(tagName, fallbackUrl, fallbackSize);
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
    /// 从 config.json 读取代理设置(与主程序「设置 → 网络代理」一致)。
    /// 同样刻意不引用 Daybreak.Core,保持更新器独立。
    /// </summary>
    private static (string? Host, int Port, string Type) ReadProxyFromConfig()
    {
        try
        {
            var configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Daybreak", "config.json");
            if (!File.Exists(configPath))
                return (null, 0, "HTTP");

            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (null, 0, "HTTP");

            string? host = root.TryGetProperty("ProxyHost", out var h) && h.ValueKind == JsonValueKind.String
                ? h.GetString()
                : null;
            var port = root.TryGetProperty("ProxyPort", out var p) && p.ValueKind == JsonValueKind.Number
                ? p.GetInt32()
                : 0;
            var type = root.TryGetProperty("ProxyType", out var t) && t.ValueKind == JsonValueKind.String
                ? (t.GetString() ?? "HTTP")
                : "HTTP";

            return (string.IsNullOrWhiteSpace(host) ? null : host, port, type);
        }
        catch
        {
            return (null, 0, "HTTP");
        }
    }

    /// <summary>构建下载用 HttpClient:应用配置的代理;未配置时沿用系统默认代理。</summary>
    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };

        var (host, port, type) = ReadProxyFromConfig();
        if (!string.IsNullOrWhiteSpace(host) && port > 0)
        {
            // SOCKS5 需要带 scheme,HTTP 代理用主机+端口即可
            handler.Proxy = type.Equals("SOCKS5", StringComparison.OrdinalIgnoreCase)
                ? new WebProxy($"socks5://{host}:{port}")
                : new WebProxy(host, port);
            handler.UseProxy = true;
            Console.WriteLine($"[Updater] 使用配置的代理:{type}://{host}:{port}");
        }

        var http = new HttpClient(handler)
        {
            // 大文件下载不能用 HttpClient 的默认 100s 总超时;由每次尝试的 CTS 控制
            Timeout = Timeout.InfiniteTimeSpan,
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Daybreak-Launcher/1.0 (Minecraft launcher)");
        return http;
    }

    /// <summary>
    /// 下载更新包:直连 → gh-proxy 镜像,每个源多次重试,且**支持断点续传**。
    /// 国内直连 GitHub Release 资产(objects.githubusercontent.com)经常中途掐断;
    /// 77MB 若每次都从头重下基本不可能成功,必须靠 Range 续传把进度保住。
    /// </summary>
    private static async Task DownloadWithFallbackAsync(string url, string destPath)
    {
        var partPath = destPath + ".part";

        var candidates = new List<string> { url };

        // gh-proxy 系镜像只对 github.com 链接有意义(格式:https://镜像/原始URL)。
        // Gitee 链接本身国内直连就快,不需要也不该套镜像。
        if (url.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            string[] mirrors = ["https://ghproxy.net/", "https://gh-proxy.com/"];
            foreach (var prefix in mirrors)
            {
                if (!url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    candidates.Add(prefix + url);
            }
        }

        using var http = CreateHttpClient();
        Exception? lastError = null;

        for (var ci = 0; ci < candidates.Count; ci++)
        {
            var candidate = candidates[ci];
            Console.WriteLine($"[Updater] 下载源 {ci + 1}/{candidates.Count}:{candidate}");

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    await DownloadCoreAsync(http, candidate, partPath);

                    if (File.Exists(destPath))
                        File.Delete(destPath);
                    File.Move(partPath, destPath);

                    var mb = new FileInfo(destPath).Length / 1024.0 / 1024.0;
                    Console.WriteLine($"[Updater] 下载完成:{mb:F1} MB" + (ci > 0 ? "(镜像源)" : ""));
                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    var have = File.Exists(partPath) ? new FileInfo(partPath).Length / 1024.0 / 1024.0 : 0;
                    Console.WriteLine(
                        $"[Updater] 第 {attempt} 次尝试失败:{ex.Message}" +
                        (have > 0 ? $"(已保留 {have:F1} MB,下次续传)" : ""));

                    if (attempt < 3)
                        await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
                }
            }
            Console.WriteLine("[Updater] 该源多次失败,换下一个源");
        }

        throw new InvalidOperationException(
            "所有下载源均失败。可在启动器「设置 → 网络代理」配置代理后重试," +
            "或到 Releases 页面用浏览器手动下载(浏览器通常能续传)。", lastError);
    }

    /// <summary>
    /// 流式下载到 .part 文件(不把 77MB 读进内存),已存在残片时用 Range 续传。
    /// 每 5MB 打一次进度;服务器不支持 Range 时自动回退为从头下载。
    /// </summary>
    private static async Task DownloadCoreAsync(HttpClient http, string url, string partPath)
    {
        var existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

        // 单次尝试 20 分钟上限;超时后由外层重试并续传
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0)
            request.Headers.Range = new RangeHeaderValue(existing, null);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.EnsureSuccessStatusCode();

        bool append;
        long total;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            // 续传:总长取自 Content-Range
            total = response.Content.Headers.ContentRange?.Length
                    ?? (existing + (response.Content.Headers.ContentLength ?? 0));
            append = true;
        }
        else
        {
            // 服务器忽略 Range(或本就没有残片):从头下载
            total = response.Content.Headers.ContentLength ?? 0;
            append = false;
            existing = 0;
        }

        // 残片比远程还大 → 是别的版本/坏文件,清掉从头来
        if (append && total > 0 && existing >= total)
        {
            try { File.Delete(partPath); } catch { /* 下次会再判 */ }
            throw new IOException($"本地残片({existing} 字节)与远程总长({total} 字节)不符,已清除并重试");
        }

        if (existing > 0)
            Console.WriteLine($"[Updater] 续传:从 {existing / 1024.0 / 1024.0:F1} MB 继续(共 {total / 1024.0 / 1024.0:F1} MB)");
        else
            Console.WriteLine(total > 0
                ? $"[Updater] 开始下载:{total / 1024.0 / 1024.0:F1} MB"
                : "[Updater] 开始下载(大小未知)");

        await using (var source = await response.Content.ReadAsStreamAsync(cts.Token))
        await using (var target = new FileStream(
            partPath,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.None, 1 << 16))
        {
            var buffer = new byte[1 << 16];
            long received = existing;
            long nextReport = received + 5L * 1024 * 1024;
            var startedAt = DateTime.UtcNow;

            while (true)
            {
                var read = await source.ReadAsync(buffer, cts.Token);
                if (read <= 0)
                    break;

                await target.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                received += read;

                if (received >= nextReport)
                {
                    var elapsed = Math.Max(1, (DateTime.UtcNow - startedAt).TotalSeconds);
                    var speed = (received - existing) / 1024.0 / 1024.0 / elapsed;
                    var pct = total > 0 ? received * 100.0 / total : 0;
                    Console.WriteLine(total > 0
                        ? $"[Updater] {received / 1024.0 / 1024.0:F1} / {total / 1024.0 / 1024.0:F1} MB ({pct:F0}%, {speed:F0} MB/s)"
                        : $"[Updater] 已下载 {received / 1024.0 / 1024.0:F1} MB ({speed:F0} MB/s)");
                    nextReport += 5L * 1024 * 1024;
                }
            }

            if (total > 0 && received < total)
                throw new IOException($"连接中断:已收 {received} / {total} 字节");
        }
    }

    /// <summary>
    /// 覆盖前校验下载包:必须是能打开的 zip,且包含主程序 exe。
    /// 防止网络中断/源返回 HTML 错误页时把安装目录覆盖成残缺状态。
    /// </summary>
    private static void ValidatePackage(string zipPath)
    {
        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(zipPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("下载的更新包不是有效的 zip(可能网络中断),已中止更新", ex);
        }

        using (archive)
        {
            var names = archive.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();
            var hasLauncher = names.Any(n =>
                n.Equals(AppName + ".exe", StringComparison.OrdinalIgnoreCase) ||
                n.EndsWith("/" + AppName + ".exe", StringComparison.OrdinalIgnoreCase));

            if (!hasLauncher)
                throw new InvalidOperationException($"更新包内容不完整(缺少 {AppName}.exe),已中止更新");

            Console.WriteLine($"[Updater] 更新包校验通过({names.Count} 个条目)");
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
