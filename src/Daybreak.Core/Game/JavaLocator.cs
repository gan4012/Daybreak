using System.IO;

namespace Daybreak.Core.Game;

/// <summary>系统 Java 探测。M1 只做定位,M2 接入按版本自动下载运行时。</summary>
public static class JavaLocator
{
    /// <summary>常见安装位置 + 环境变量,返回找到的全部 java.exe(按优先级)。</summary>
    public static IReadOnlyList<string> FindJavas(string? configuredDirectory)
    {
        var results = new List<string>();

        void AddIfJava(string? directory)
        {
            if (string.IsNullOrEmpty(directory))
                return;
            var candidate = Path.Combine(directory, "bin", "java.exe");
            if (File.Exists(candidate) && !results.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                results.Add(candidate);
        }

        // 用户在设置里手动指定的 Java 目录优先
        AddIfJava(configuredDirectory);

        var env = Environment.GetEnvironmentVariable("JAVA_HOME");
        AddIfJava(env);

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var root in new[] { programFiles, Path.Combine(programFiles, " (x86)") })
        {
            foreach (var vendor in new[] { "Java", "Eclipse Adoptium", "Microsoft", "Zulu", "BellSoft", "Amazon Corretto" })
            {
                var vendorDir = Path.Combine(root, vendor);
                if (!Directory.Exists(vendorDir))
                    continue;
                foreach (var dir in Directory.EnumerateDirectories(vendorDir))
                    AddIfJava(dir);
            }
        }

        return results;
    }

    public static string? FindJava(string? configuredDirectory)
    {
        var all = FindJavas(configuredDirectory);
        return all.Count > 0 ? all[0] : null;
    }
}
