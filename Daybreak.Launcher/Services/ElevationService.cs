using System.Diagnostics;
using System.Security.Principal;

namespace Daybreak.Launcher.Services;

/// <summary>
/// 管理员权限检测与按需提权重启。
///
/// 为什么需要:EasyTier 创建虚拟网卡(TUN)必须有管理员权限。
/// 但我们**不想让整个启动器一直要求管理员** —— 那会让每个用户每次开启动器都弹 UAC,
/// 而且游戏进程也会继承管理员身份(部分反作弊/存档权限会出问题)。
///
/// 所以采用"按需提权":平时普通权限运行;用户真正要用联机时,
/// 提示一次并以管理员身份重新拉起自己(见 <see cref="TryRestartElevated"/>)。
/// </summary>
public static class ElevationService
{
    /// <summary>当前进程是否已是管理员。</summary>
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 以管理员身份重新启动当前程序。
    /// 成功启动后调用方应自行退出当前实例。
    /// 用户点了 UAC 的"否"会抛 <see cref="System.ComponentModel.Win32Exception"/>(1223)。
    /// </summary>
    public static bool TryRestartElevated()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
            return false;

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = true,
            Verb = "runas",      // 触发 UAC
        };

        // 把原始启动参数带过去
        var args = Environment.GetCommandLineArgs();
        for (var i = 1; i < args.Length; i++)
            psi.ArgumentList.Add(args[i]);

        try
        {
            Process.Start(psi);
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 1223 = 用户在 UAC 对话框点了"否"
            return false;
        }
    }
}