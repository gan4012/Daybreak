using System.IO;
using System.Text;

namespace Daybreak.Core.Server;

/// <summary>
/// EULA(eula.txt)处理。
///
/// 合规要点:**绝不代替用户同意**。Mojang 的 EULA 要求用户本人明确接受,
/// 所以这里只做「检测 / 写入 / 撤回」,是否同意由 UI 上用户主动勾选决定,
/// 且不提供任何"自动勾选"或静默写入的路径。
/// </summary>
public static class EulaService
{
    public const string EulaUrl = "https://aka.ms/MinecraftEULA";

    public static string EulaPath(string serverDirectory) => Path.Combine(serverDirectory, "eula.txt");

    /// <summary>读取当前是否已同意(文件不存在视为未同意)。</summary>
    public static bool IsAccepted(string serverDirectory)
    {
        var path = EulaPath(serverDirectory);
        if (!File.Exists(path))
            return false;

        foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#'))
                continue;

            var sep = trimmed.IndexOf('=');
            if (sep <= 0)
                continue;

            if (trimmed[..sep].Trim().Equals("eula", StringComparison.OrdinalIgnoreCase))
                return trimmed[(sep + 1)..].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>写入 eula=true。只应在用户明确同意后调用。</summary>
    public static void Accept(string serverDirectory)
    {
        Directory.CreateDirectory(serverDirectory);
        var content =
            "#By changing the setting below to TRUE you are indicating your agreement to our EULA (" +
            EulaUrl + ")." + Environment.NewLine +
            "#由 Daybreak 破晓启动器在用户确认后写入" + Environment.NewLine +
            "eula=true" + Environment.NewLine;

        File.WriteAllText(EulaPath(serverDirectory), content, new UTF8Encoding(false));
    }

    /// <summary>撤回同意(写回 false 而不是删除文件,便于用户看出发生过什么)。</summary>
    public static void Revoke(string serverDirectory)
    {
        var path = EulaPath(serverDirectory);
        if (!File.Exists(path))
            return;

        var lines = File.ReadAllLines(path, Encoding.UTF8);
        var replaced = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('#') || !trimmed.StartsWith("eula", StringComparison.OrdinalIgnoreCase))
                continue;

            lines[i] = "eula=false";
            replaced = true;
        }

        if (!replaced)
            lines = [.. lines, "eula=false"];

        File.WriteAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine,
            new UTF8Encoding(false));
    }
}