using Daybreak.Core.Diagnostics;
using Xunit;

namespace Daybreak.Core.Tests;

public class CrashAnalyzerTests
{
    private static string WriteGameDir(Dictionary<string, string> files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "daybreak-crash-test", Guid.NewGuid().ToString("N"));
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(dir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        return dir;
    }

    [Fact]
    public void Analyze_NormalExit_NoFindings()
    {
        var dir = WriteGameDir(new Dictionary<string, string>());
        var report = CrashAnalyzer.Analyze(dir, exitCode: 0);
        Assert.False(report.Crashed);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void Analyze_OldJavaCrash_MatchesRule()
    {
        var dir = WriteGameDir(new Dictionary<string, string>
        {
            ["crash-reports/crash-2026-01-01.txt"] =
                "java.lang.UnsupportedClassVersionError: net/minecraft/client/main/Main has been compiled by a more recent version of the Java Runtime (class file version 61.0)",
        });
        var report = CrashAnalyzer.Analyze(dir, exitCode: 1);

        Assert.True(report.Crashed);
        Assert.Contains(report.Findings, f => f.Title == "Java 版本过旧");
    }

    [Fact]
    public void Analyze_OutOfMemory_MatchesRule()
    {
        var dir = WriteGameDir(new Dictionary<string, string>
        {
            ["logs/latest.log"] = "java.lang.OutOfMemoryError: Java heap space",
        });
        var report = CrashAnalyzer.Analyze(dir, exitCode: -1);
        Assert.Contains(report.Findings, f => f.Title == "内存不足");
    }

    [Fact]
    public void Analyze_MissingDependency_MatchesRule()
    {
        var dir = WriteGameDir(new Dictionary<string, string>
        {
            ["logs/latest.log"] =
                "ModResolutionException: Missing or unsupported mandatory dependencies:\nMod 'fabric-api' requires fabric-loader",
        });
        var report = CrashAnalyzer.Analyze(dir, exitCode: 1);
        Assert.Contains(report.Findings, f => f.Title == "缺少前置 Mod 或依赖");
    }

    [Fact]
    public void Analyze_UnknownCrash_GivesGenericAdvice()
    {
        var dir = WriteGameDir(new Dictionary<string, string>
        {
            ["crash-reports/crash-x.txt"] = "Something totally weird happened 0xDEADBEEF",
        });
        var report = CrashAnalyzer.Analyze(dir, exitCode: 1);

        var finding = Assert.Single(report.Findings);
        Assert.Equal("未知崩溃", finding.Title);
        Assert.Contains("crash-x.txt", finding.Advice);
    }

    [Fact]
    public void Analyze_MultiplePatterns_MatchesMultipleRules()
    {
        var dir = WriteGameDir(new Dictionary<string, string>
        {
            ["logs/latest.log"] =
                "java.lang.OutOfMemoryError\njava.net.ConnectException: connection refused",
        });
        var report = CrashAnalyzer.Analyze(dir, exitCode: 1);
        Assert.True(report.Findings.Count >= 2);
    }
}
