# 录屏工具(基于 ffmpeg gdigrab)
#
# 为什么不用 OBS / Win+G:
#   这个脚本会在录制前把目标窗口强制置顶，录完恢复。
#   否则别的窗口压在启动器上面时，录出来的画面里会混进别人的内容
#   (实测踩过:录屏时把浏览器里的私人页面录了进去)。
#
# 用法:
#   # 录启动器窗口 30 秒
#   powershell -File docs/video/tools/record.ps1 -Window "Daybreak" -Duration 30 -Out A1.mp4
#
#   # 不限时,按回车停止
#   powershell -File docs/video/tools/record.ps1 -Window "Daybreak" -Out A1.mp4
#
#   # 录整个主屏(不用窗口标题)
#   powershell -File docs/video/tools/record.ps1 -FullScreen -Duration 20 -Out X.mp4
#
#   # 录制前留 5 秒准备时间
#   powershell -File docs/video/tools/record.ps1 -Window "Daybreak" -Duration 30 -Countdown 5 -Out A1.mp4

param(
    [string]$Window = "",
    [switch]$FullScreen,
    [int]$Duration = 0,          # 0 = 手动停止
    [int]$Countdown = 3,
    [int]$Fps = 30,
    [string]$Out = "record.mp4",
    [int]$Crf = 18
)

$ErrorActionPreference = "Stop"

Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class RecWin {
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

function Get-FFmpeg {
    $cmd = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $cand = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Recurse -Filter "ffmpeg.exe" -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($cand) { return $cand.FullName }
    throw "找不到 ffmpeg。请先执行: winget install --id Gyan.FFmpeg -e"
}

$ffmpeg = Get-FFmpeg
$outPath = [System.IO.Path]::GetFullPath($Out)
$outDir = Split-Path $outPath -Parent
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

# ---- 定位窗口 ----
$hwnd = [IntPtr]::Zero
$region = @{ X = 0; Y = 0; W = 0; H = 0 }

if ($FullScreen) {
    Add-Type -AssemblyName System.Windows.Forms
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $region = @{ X = $b.X; Y = $b.Y; W = $b.Width; H = $b.Height }
    Write-Host "==> 录制整个主屏 $($region.W)x$($region.H)" -ForegroundColor Cyan
}
else {
    if (-not $Window) { throw "必须指定 -Window <窗口标题关键词> 或 -FullScreen" }

    $procs = Get-Process | Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle -like "*$Window*" }
    if (-not $procs) { throw "找不到标题含「$Window」的窗口" }
    $proc = $procs | Select-Object -First 1
    $hwnd = $proc.MainWindowHandle

    [RecWin]::ShowWindow($hwnd, 9) | Out-Null            # SW_RESTORE
    Start-Sleep -Milliseconds 300
    [RecWin]::SetWindowPos($hwnd, [RecWin]::HWND_TOPMOST, 0, 0, 0, 0,
        ([RecWin]::SWP_NOMOVE -bor [RecWin]::SWP_NOSIZE -bor [RecWin]::SWP_SHOWWINDOW)) | Out-Null
    [RecWin]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 600

    $r = New-Object RecWin+R
    [RecWin]::GetWindowRect($hwnd, [ref]$r) | Out-Null
    $region = @{ X = $r.L; Y = $r.T; W = ($r.Rr - $r.L); H = ($r.B - $r.T) }
    Write-Host "==> 录制窗口: $($proc.MainWindowTitle)" -ForegroundColor Cyan
    Write-Host "    区域 $($region.X),$($region.Y)  $($region.W)x$($region.H)  (已强制置顶)" -ForegroundColor Cyan
}

# ---- 倒计时 ----
if ($Countdown -gt 0) {
    for ($i = $Countdown; $i -ge 1; $i--) {
        Write-Host "    $i…" -NoNewline
        Start-Sleep -Seconds 1
    }
    Write-Host " 开始"
}

# ---- 录制 ----
# gdigrab 按屏幕区域采集，所以前面的置顶步骤是必须的
$args = @(
    "-y", "-v", "error",
    "-f", "gdigrab",
    "-framerate", "$Fps",
    "-offset_x", "$($region.X)",
    "-offset_y", "$($region.Y)",
    "-video_size", "$($region.W)x$($region.H)",
    "-draw_mouse", "1",
    "-i", "desktop",
    "-c:v", "libx264", "-preset", "ultrafast", "-crf", "$Crf",
    "-pix_fmt", "yuv420p",
    "-movflags", "+faststart"
)
if ($Duration -gt 0) { $args += @("-t", "$Duration") }
$args += $outPath

$procFF = Start-Process -FilePath $ffmpeg -ArgumentList $args -PassThru -NoNewWindow -RedirectStandardError "$outDir\_ffmpeg.log"

if ($Duration -gt 0) {
    Write-Host "==> 录制中(${Duration}s)…" -ForegroundColor Yellow
    $procFF.WaitForExit(($Duration + 30) * 1000) | Out-Null
}
else {
    Write-Host "==> 录制中… 按回车停止" -ForegroundColor Yellow
    [Console]::ReadLine() | Out-Null
    # ffmpeg 收到 q 会正常收尾(写好文件头尾)
    try {
        $procFF.StandardInput.WriteLine("q")
        $procFF.WaitForExit(15000) | Out-Null
    } catch { }
    if (-not $procFF.HasExited) { $procFF.Kill() }
}

if (-not $procFF.HasExited) { $procFF.Kill(); Start-Sleep -Milliseconds 500 }

# ---- 恢复窗口层级 ----
if ($hwnd -ne [IntPtr]::Zero) {
    [RecWin]::SetWindowPos($hwnd, [RecWin]::HWND_NOTOPMOST, 0, 0, 0, 0,
        ([RecWin]::SWP_NOMOVE -bor [RecWin]::SWP_NOSIZE)) | Out-Null
}

# ---- 结果 ----
if (Test-Path $outPath) {
    $size = [math]::Round((Get-Item $outPath).Length / 1MB, 2)
    $probe = Join-Path (Split-Path $ffmpeg -Parent) "ffprobe.exe"
    $dur = ""
    if (Test-Path $probe) {
        $dur = (& $probe -v error -show_entries format=duration -of csv=p=0 $outPath 2>&1 | Select-Object -First 1)
    }
    Write-Host ""
    Write-Host "==> 完成: $outPath" -ForegroundColor Green
    Write-Host "    $size MB  ${dur}s"
    Write-Host ""
    Write-Host "    先播一遍确认画面干净(没有录进别人的窗口):" -ForegroundColor Cyan
    Write-Host "    ffplay `"$outPath`""
}
else {
    Write-Host "==> 录制失败，ffmpeg 日志:" -ForegroundColor Red
    Get-Content "$outDir\_ffmpeg.log" -ErrorAction SilentlyContinue | Select-Object -Last 10
}