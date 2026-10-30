param([int]$dx, [int]$dy)
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Mouse {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx2, uint dy2, uint d, UIntPtr e);
    public struct RECT { public int Left, Top, Right, Bottom; }
}
'@
$proc = Get-Process Daybreak.Launcher | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
[Mouse]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 600
$r = New-Object Mouse+RECT
[Mouse]::GetWindowRect($proc.MainWindowHandle, [ref]$r) | Out-Null
$x = $r.Left + $dx; $y = $r.Top + $dy
[Mouse]::SetCursorPos($x, $y) | Out-Null
Start-Sleep -Milliseconds 200
[Mouse]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)  # LEFTDOWN
[Mouse]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)  # LEFTUP
Write-Output "clicked $x,$y"
