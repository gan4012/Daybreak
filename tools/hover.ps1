param([int]$dx, [int]$dy)
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Hov {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    public struct RECT { public int Left, Top, Right, Bottom; }
}
'@
$proc = Get-Process Daybreak.Launcher | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
[Hov]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 500
$r = New-Object Hov+RECT
[Hov]::GetWindowRect($proc.MainWindowHandle, [ref]$r) | Out-Null
[Hov]::SetCursorPos($r.Left + $dx, $r.Top + $dy) | Out-Null
Start-Sleep -Milliseconds 400
Write-Output "hovered $($r.Left + $dx),$($r.Top + $dy)"
