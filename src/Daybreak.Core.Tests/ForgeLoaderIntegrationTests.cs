using Daybreak.Core.Game;
using Daybreak.Core.Mod;
using Xunit;
using Xunit.Abstractions;

namespace Daybreak.Core.Tests;

/// <summary>
/// Forge / NeoForge 真实安装集成测试(联网,耗时较长)。
/// 覆盖此前"管线与 Fabric 相同、风险低"但从未实跑过的路径:
/// Forge 的 processors 执行链需要真实 Java 参与,是最容易出问题的一环。
/// </summary>
[Trait("Category", "Integration")]
public class ForgeLoaderIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public ForgeLoaderIntegrationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task InstallForge_1_20_1_RunsProcessorsAndProducesLaunchableVersion()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "daybreak-forge-test", Guid.NewGuid().ToString("N"));
        const string gameVersion = "1.20.1";

        var launchService = new LaunchService(tempDir, DownloadMirror.Official);
        var loaderService = new LoaderService(tempDir, DownloadMirror.Official);
        var progress = new Progress<LaunchProgress>(p =>
        {
            if (!string.IsNullOrEmpty(p.CurrentFile))
                _output.WriteLine($"  [{p.ProgressedTasks}/{p.TotalTasks}] {p.CurrentFile}");
        });

        try
        {
            // 1. 选一个可用 Forge 版本(优先推荐版)
            var forgeVersions = await loaderService.GetForgeVersionsAsync(gameVersion);
            Assert.NotEmpty(forgeVersions);
            var forge = forgeVersions.FirstOrDefault(v => v.Recommended) ?? forgeVersions[0];
            _output.WriteLine($"选用 Forge {forge.Name}(推荐={forge.Recommended},最新={forge.Latest})");

            // 2. 先装原版:Forge 安装器依赖原版版本 JSON 与客户端 jar
            _output.WriteLine("安装原版…");
            await Task.Run(() => launchService.InstallAsync(gameVersion, progress));

            // 3. 取该版本要求的 Java(1.20.1 需要 Java 17)
            _output.WriteLine("解析 Java 运行时…");
            var javaPath = await launchService.EnsureJavaAsync(gameVersion, null, progress);
            Assert.False(string.IsNullOrWhiteSpace(javaPath), "未能取得可用 Java");
            Assert.True(File.Exists(javaPath), $"Java 不存在:{javaPath}");
            _output.WriteLine($"Java: {javaPath}");

            // 4. 装 Forge(含 processors 执行链)
            _output.WriteLine("安装 Forge(含 processors,耗时较长)…");
            var installedName = await Task.Run(() =>
                loaderService.InstallForgeAsync(gameVersion, forge.Name, javaPath, progress));
            _output.WriteLine($"Forge 安装返回版本名:{installedName}");

            // 5. 校验产物
            var versionsDir = Path.Combine(tempDir, "versions");
            Assert.True(Directory.Exists(versionsDir), "versions 目录不存在");

            var targetDir = Path.Combine(versionsDir, installedName);
            var jsonPath = Path.Combine(targetDir, installedName + ".json");
            Assert.True(File.Exists(jsonPath),
                $"版本 JSON 缺失:{jsonPath}\nversions 目录树:\n{DescribeTree(versionsDir)}");

            var json = await File.ReadAllTextAsync(jsonPath);
            Assert.Contains("forge", json, StringComparison.OrdinalIgnoreCase);

            // 现代 Forge/NeoForge 的版本目录里**只有 JSON**:
// 客户端 jar 通过 inheritsFrom 指向原版版本(Forge 只是往 libraries 里放 patch 产物)。
            // 所以必须沿继承链解析,不能直接找同名 jar。
            var jarPath = ResolveClientJar(versionsDir, installedName);
            Assert.False(string.IsNullOrEmpty(jarPath),
                $"沿继承链未找到客户端 jar。\nversions 目录树:\n{DescribeTree(versionsDir)}");
            Assert.True(File.Exists(jarPath),
                $"客户端 jar 不存在:{jarPath}\nversions 目录树:\n{DescribeTree(versionsDir)}");
            Assert.True(new FileInfo(jarPath).Length > 10_000, "客户端 jar 过小");

            // 版本能被启动器识别
            var installed = launchService.GetInstalledVersions();
            Assert.Contains(installedName, installed);
            _output.WriteLine($"✅ Forge 安装成功,已识别版本:{installedName}(客户端 jar={Path.GetRelativePath(versionsDir, jarPath)})");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* 清理失败不影响断言 */ }
        }
    }

    [Fact]
    public async Task InstallNeoForge_1_21_1_ProducesLaunchableVersion()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "daybreak-neoforge-test", Guid.NewGuid().ToString("N"));
        const string gameVersion = "1.21.1";

        // 预检:CmlLib 的 NeoForgeInstaller 只接受 MinecraftLauncher,
        // 内部自带 HttpClient,不走我们的 ResilientHttpHandler,
        // 因此镜像改写与重试都帮不上忙。国内直连 maven.neoforged.net 常在此阶段挂死,
        // 先探测一次,失败时给出可执行的排查建议而不是一句超时。
        var mavenReachable = await IsNeoForgeMavenReachableAsync();
        Assert.True(mavenReachable,
            "无法访问 maven.neoforged.net(NeoForge 的 Maven 仓库)。\n" +
            "说明:CmlLib 的 NeoForge 安装器内部使用自己的 HttpClient,不经过启动器的镜像/重试处理器,\n" +
            "      所以设置里的 BMCLAPI 镜像对 NeoForge 安装无效(Forge 不受影响)。\n" +
            "可选排查:① 在「设置 → 网络代理」配置可用代理;② 换到能直连该域名的网络后重跑。\n" +
            "已确认 BMCLAPI 镜像了该仓库(https://bmclapi2.bangbang93.com/maven/net/neoforged/...),但需 CmlLib 侧支持注入。");

        var launchService = new LaunchService(tempDir, DownloadMirror.Official);
        var loaderService = new LoaderService(tempDir, DownloadMirror.Official);
        var progress = new Progress<LaunchProgress>(p =>
        {
            if (!string.IsNullOrEmpty(p.CurrentFile))
                _output.WriteLine($"  [{p.ProgressedTasks}/{p.TotalTasks}] {p.CurrentFile}");
        });

        try
        {
            var neoVersions = await loaderService.GetNeoForgeVersionsAsync(gameVersion);
            Assert.NotEmpty(neoVersions);
            var neo = neoVersions[0];
            _output.WriteLine($"选用 NeoForge {neo.Name}");

            _output.WriteLine("安装原版…");
            await Task.Run(() => launchService.InstallAsync(gameVersion, progress));

            _output.WriteLine("解析 Java 运行时…");
            var javaPath = await launchService.EnsureJavaAsync(gameVersion, null, progress);
            Assert.False(string.IsNullOrWhiteSpace(javaPath), "未能取得可用 Java");
            Assert.True(File.Exists(javaPath), $"Java 不存在:{javaPath}");
            _output.WriteLine($"Java: {javaPath}");

            _output.WriteLine("安装 NeoForge…");
            var installedName = await Task.Run(() =>
                loaderService.InstallNeoForgeAsync(gameVersion, neo.Name, javaPath, progress));
            _output.WriteLine($"NeoForge 安装返回版本名:{installedName}");

            var versionsDir = Path.Combine(tempDir, "versions");
            var targetDir = Path.Combine(versionsDir, installedName);
            var jsonPath = Path.Combine(targetDir, installedName + ".json");
            Assert.True(File.Exists(jsonPath),
                $"版本 JSON 缺失:{jsonPath}\nversions 目录树:\n{DescribeTree(versionsDir)}");

            var json = await File.ReadAllTextAsync(jsonPath);
            Assert.Contains("neoforge", json, StringComparison.OrdinalIgnoreCase);

            // 与 Forge 同理:现代加载器版本目录只有 JSON,jar 沿 inheritsFrom 解析
            var jarPath = ResolveClientJar(versionsDir, installedName);
            Assert.False(string.IsNullOrEmpty(jarPath),
                $"沿继承链未找到客户端 jar。\nversions 目录树:\n{DescribeTree(versionsDir)}");
            Assert.True(File.Exists(jarPath),
                $"客户端 jar 不存在:{jarPath}\nversions 目录树:\n{DescribeTree(versionsDir)}");

            var installed = launchService.GetInstalledVersions();
            Assert.Contains(installedName, installed);
            _output.WriteLine($"✅ NeoForge 安装成功,已识别版本:{installedName}(客户端 jar={Path.GetRelativePath(versionsDir, jarPath)})");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* 清理失败不影响断言 */ }
        }
    }

    /// <summary>探测 NeoForge 的 Maven 是否可达(只取元数据,开销很小)。</summary>
    private static async Task<bool> IsNeoForgeMavenReachableAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var response = await http.GetAsync(
                "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml",
                HttpCompletionOption.ResponseHeadersRead);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 沿 inheritsFrom 链解析出实际使用的客户端 jar 路径。
    /// 现代 Forge/NeoForge 不自带 jar:版本目录只有 JSON,jar 由原版版本提供。
    /// </summary>
    private static string? ResolveClientJar(string versionsDir, string versionName, int depth = 0)
    {
        if (depth > 5)
            return null;

        var dir = Path.Combine(versionsDir, versionName);
        var jsonPath = Path.Combine(dir, versionName + ".json");
        if (!File.Exists(jsonPath))
            return null;

        string? inheritsFrom = null;
        string? jarField = null;
        using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(jsonPath)))
        {
            if (doc.RootElement.TryGetProperty("inheritsFrom", out var inh) && inh.ValueKind == System.Text.Json.JsonValueKind.String)
                inheritsFrom = inh.GetString();
            if (doc.RootElement.TryGetProperty("jar", out var jar) && jar.ValueKind == System.Text.Json.JsonValueKind.String)
                jarField = jar.GetString();
        }

        // 先看本目录有没有 jar(原版版本就是这种情况)
        var candidateName = jarField ?? versionName;
        var localJar = Path.Combine(dir, candidateName + ".jar");
        if (File.Exists(localJar))
            return localJar;

        // 本目录没有 → 沿继承链往父版本找
        if (!string.IsNullOrEmpty(inheritsFrom))
            return ResolveClientJar(versionsDir, inheritsFrom, depth + 1);

        // 退一步:同目录同名 jar
        var sameName = Path.Combine(dir, versionName + ".jar");
        return File.Exists(sameName) ? sameName : null;
    }

    /// <summary>列出目录树用于断言失败时定位(最多 2 层)。</summary>
    private static string DescribeTree(string root)
    {
        if (!Directory.Exists(root))
            return "(目录不存在)";

        var lines = new List<string>();
        foreach (var dir in Directory.GetDirectories(root))
        {
            lines.Add($"  {Path.GetFileName(dir)}/");
            foreach (var file in Directory.GetFiles(dir))
                lines.Add($"    {Path.GetFileName(file)} ({new FileInfo(file).Length / 1024} KB)");
        }
        foreach (var file in Directory.GetFiles(root))
            lines.Add($"  {Path.GetFileName(file)} ({new FileInfo(file).Length / 1024} KB)");

        return lines.Count == 0 ? "(空)" : string.Join("\n", lines);
    }
}