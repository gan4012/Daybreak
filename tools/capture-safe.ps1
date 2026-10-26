param(
    [string]$OutFile = "app.png"
)

# 安全截图:只拍 Daybreak 窗口。
#
# 两个必须处理的坑:
# 1. 其它窗口可能压在启动器上面 -> 用 SetWindowPos(HWND_TOPMOST) 强制置顶,
#    只用 SetForegroundWindow 不够(实测会报告成功但 z-order 没变)。
# 2. 仅按窗口矩形抓屏会连同一区域的其它窗口一起拍进来 -> 曾因此截到用户的私人页面。
#    所以:置顶 + 校验前台 + 拍完立即恢复正常层级。

Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class SafeCap2 {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    public struct R { public int L, T, Rr, B; }

    public static readonly IntPtr HWND_TOPMOST   = new IntPtr(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    public const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_SHOWWINDOW = 0x0040;
}
'@
Add-Type -AssemblyName System.Drawing

$proc = Get-Process Daybreak.Launcher -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { Write-Output "ERROR: 启动器窗口不存在"; exit 1 }

$hwnd = $proc.MainWindowHandle

[SafeCap2]::ShowWindow($hwnd, 9) | Out-Null          # SW_RESTORE
Start-Sleep -Milliseconds 400

# 强制置顶 + 前置
[SafeCap2]::SetWindowPos($hwnd, [SafeCap2]::HWND_TOPMOST, 0, 0, 0, 0,
    ([SafeCap2]::SWP_NOMOVE -bor [SafeCap2]::SWP_NOSIZE -bor [SafeCap2]::SWP_SHOWWINDOW)) | Out-Null
[SafeCap2]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 1000

# 校验前台
$fg = [SafeCap2]::GetForegroundWindow()
$sb = New-Object System.Text.StringBuilder 256
[SafeCap2]::GetWindowText($fg, $sb, 256) | Out-Null
$title = $sb.ToString()

$result = $null
try {
    if ($fg -ne $hwnd -or $title -notlike "*Daybreak*") {
        $result = "ERROR: 启动器未获得焦点,拒绝截图(当前前台: $title)"
    }
    else {
        $r = New-Object SafeCap2+R
        [SafeCap2]::GetWindowRect($hwnd, [ref]$r) | Out-Null
        $w = $r.Rr - $r.L
        $h = $r.B - $r.T
        if ($w -le 0 -or $h -le 0) {
            $result = "ERROR: 窗口尺寸异常 ${w}x${h}"
        }
        else {
            $bmp = New-Object System.Drawing.Bitmap $w, $h
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            $g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size $w, $h))
            $bmp.Save($OutFile)
            $g.Dispose(); $bmp.Dispose()
            $result = "OK: $OutFile (${w}x${h})"
        }
    }
}
finally {
    # 恢复普通层级,不要留下一个置顶窗口
    [SafeCap2]::SetWindowPos($hwnd, [SafeCap2]::HWND_NOTOPMOST, 0, 0, 0, 0,
        ([SafeCap2]::SWP_NOMOVE -bor [SafeCap2]::SWP_NOSIZE)) | Out-Null
}

Write-Output $result
