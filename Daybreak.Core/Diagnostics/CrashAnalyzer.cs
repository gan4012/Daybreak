using System.IO;
using System.Text.RegularExpressions;

namespace Daybreak.Core.Diagnostics;

/// <summary>单条诊断结论。</summary>
public record CrashFinding(string Title, string Advice);

/// <summary>崩溃分析结果。</summary>
public record CrashReport(
    bool Crashed,
    int ExitCode,
    string? CrashReportPath,
    IReadOnlyList<CrashFinding> Findings);

/// <summary>
/// 游戏崩溃诊断:扫描 crash-reports 与 latest.log,按特征规则给出人话建议。
/// 规则来源于 Minecraft 社区常见崩溃形态。
/// </summary>
public static partial class CrashAnalyzer
{
    [GeneratedRegex(@"UnsupportedClassVersionError|class file version (?:5[0-9]|6[0-9])\.")]
    private static partial Regex OldJava();

    [GeneratedRegex(@"InaccessibleObjectException|Unable to make (?:field|method) .+ accessible")]
    private static partial Regex TooNewJava();

    [GeneratedRegex(@"OutOfMemoryError|Running the game with less than")]
    private static partial Regex OutOfMemory();

    [GeneratedRegex(@"Could not reserve enough space for object heap")]
    private static partial Regex HeapTooLarge();

    [GeneratedRegex(@"no lwjgl in java\.library\.path|Failed to load the OpenGL library|Couldn't set pixel format")]
    private static partial Regex Lwjgl();

    [GeneratedRegex(@"OpenGL is not supported by the video driver|GLX error|Pixel format not accelerated")]
    private static partial Regex GpuDriver();

    [GeneratedRegex(@"Mixin apply failed|MixinTransformerError|Critical injection failure")]
    private static partial Regex MixinConflict();

    [GeneratedRegex(@"Duplicate mods found")]
    private static partial Regex DuplicateMods();

    [GeneratedRegex(@"Missing or unsupported mandatory dependencies|Missing mods|ModResolutionException")]
    private static partial Regex MissingDependency();

    [GeneratedRegex(@"AccessControlException|Access is denied|ProviderLocalDatabase")]
    private static partial Regex AccessDenied();

    [GeneratedRegex(@"java\.net\.ConnectException|UnknownHostException|Connection refused")]
    private static partial Regex Network();

    [GeneratedRegex(@"Failed to verify username|Invalid session|Bad user name or password|Invalid credentials")]
    private static partial Regex SessionInvalid();

    private static readonly (Func<Regex> Pattern, string Title, string Advice)[] Rules =
    [
        (OldJava, "Java 版本过旧",
         "该游戏版本需要更新的 Java。回到首页点「下载 Java」自动获取匹配的运行时,或在设置中指定。"),
        (TooNewJava, "Java 版本过新",
         "部分老版本与新版 Java 的模块限制冲突。建议在设置中指定与游戏版本匹配的 Java(如 8 或 17)。"),
        (OutOfMemory, "内存不足",
         "游戏运行内存耗尽。到设置页调大内存分配;若已很大,尝试关闭其他程序或减少 Mod。"),
        (HeapTooLarge, "内存分配过大",
         "系统无法分配这么多内存(常见于 32 位 Java)。到设置页调小内存,或更换 64 位 Java。"),
        (Lwjgl, "LWJGL/OpenGL 库加载失败",
         "通常是显卡驱动问题。更新显卡驱动;若使用双显卡笔记本,尝试切换独显运行。"),
        (GpuDriver, "显卡驱动过旧",
         "游戏要求的 OpenGL 特性不受支持。请到显卡官网更新驱动(不要用第三方驱动管家)。"),
        (MixinConflict, "Mod 冲突或版本不匹配",
         "Mixin 注入失败多为 Mod 之间或 Mod 与游戏版本不兼容。尝试移除最近安装的 Mod 再启动。"),
        (DuplicateMods, "Mod 重复安装",
         "mods 目录中存在同一 Mod 的多个版本。到版本管理页删除多余的 jar(保留最新)。"),
        (MissingDependency, "缺少前置 Mod 或依赖",
         "崩溃日志提示缺少依赖。按日志中的名字到资源市场搜索安装,或移除依赖它的 Mod。"),
        (AccessDenied, "文件访问被拒绝",
         "常见于杀毒软件拦截。将游戏目录加入杀毒软件白名单后重试。"),
        (Network, "网络连接失败",
         "无法连接所需服务器。检查网络与代理设置;使用外置登录时确认服务器可访问。"),
        (SessionInvalid, "登录凭据失效",
         "账号令牌已过期或无效。到首页重新登录后再启动。"),
    ];

    /// <summary>
    /// 分析一次游戏运行。exitCode 为 0 视为正常退出,仅做常规检查;
    /// 非 0 时在崩溃报告与日志中匹配规则。
    /// </summary>
    public static CrashReport Analyze(string gameRoot, int exitCode)
    {
        var findings = new List<CrashFinding>();
        var crashReportPath = FindNewestFile(Path.Combine(gameRoot, "crash-reports"), "*.txt");

        if (exitCode != 0)
        {
            var logText = ReadText(crashReportPath) ?? ReadText(Path.Combine(gameRoot, "logs", "latest.log")) ?? "";

            foreach (var (pattern, title, advice) in Rules)
            {
                if (pattern().IsMatch(logText))
                    findings.Add(new CrashFinding(title, advice));
            }

            if (findings.Count == 0)
                findings.Add(new CrashFinding(
                    "未知崩溃",
                    $"已保存崩溃报告:{(crashReportPath is null ? "未找到,请查看 logs/latest.log" : crashReportPath)}。" +
                    "可尝试:1) 移除最近安装的 Mod;2) 校验游戏文件完整性;3) 带着崩溃报告反馈。"));
        }

        return new CrashReport(exitCode != 0, exitCode, crashReportPath, findings);
    }

    private static string? FindNewestFile(string directory, string pattern)
    {
        if (!Directory.Exists(directory))
            return null;

        var file = new DirectoryInfo(directory)
            .EnumerateFiles(pattern)
            .OrderByDescending(f => f.LastWriteTime)
            .FirstOrDefault();
        return file?.FullName;
    }

    private static string? ReadText(string? path)
    {
        if (path is null || !File.Exists(path))
            return null;
        try
        {
            // 崩溃报告可能很大,只取前后各 64KB 足以命中规则
            using var stream = File.OpenRead(path);
            var length = Math.Min(stream.Length, 128 * 1024);
            var buffer = new byte[length];
            stream.ReadExactly(buffer, 0, buffer.Length);
            return System.Text.Encoding.UTF8.GetString(buffer);
        }
        catch (IOException)
        {
            return null;
        }
    }
}
