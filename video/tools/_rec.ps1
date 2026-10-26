# 录制辅助(_rec.ps1)—— 被 dot-source 使用，不是独立脚本
#
# 提供 Invoke-Clip:录一段固定时长的片段，期间执行你给的动作。
#
#   . docs\video\tools\_rec.ps1
#   Invoke-Clip -Out "clips\A1-首页.mp4" -Duration 4
#   Invoke-Clip -Out "clips\A3-登录方式.mp4" -Duration 7 -Action { & .\uia-invoke.ps1 -name "离线登录" }
#
# 为什么固定时长而不是"按键停止":
#   ffmpeg 干净收尾需要往 stdin 写 q，PowerShell 里拿到可写的 stdin 很别扭，
#   而 Start-Process 杀进程可能留下损坏的 mp4。固定时长最稳。
#   代价是头尾各有 1-2 秒余量 —— 这对剪辑反而是好事(有裁剪余量)。

$script:FFmpeg = $null

function Get-FFmpegPath {
    if ($script:FFmpeg) { return $script:FFmpeg }
    $cmd = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if ($cmd) { $script:FFmpeg = $cmd.Source; return $script:FFmpeg }
    $cand = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Recurse -Filter "ffmpeg.exe" -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($cand) { $script:FFmpeg = $cand.FullName; return $script:FFmpeg }
    throw "找不到 ffmpeg"
}

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ClipWin {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    public struct R { public int L, T, Rr, B; }
    public static readonly IntPtr TOPMOST = new IntPtr(-1);
    public static readonly IntPtr NOTOPMOST = new IntPtr(-2);
    public const uint NOMOVE = 0x0002, NOSIZE = 0x0001, SHOW = 0x0040;
}
'@ -ErrorAction SilentlyContinue

function Get-LauncherWindow {
    param([string]$Match = "Daybreak")
    $procs = Get-Process | Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle -like "*$Match*" }
    if (-not $procs) { throw "找不到标题含「$Match」的窗口" }
    $p = $procs | Select-Object -First 1
    return @{ Handle = $p.MainWindowHandle; Title = $p.MainWindowTitle }
}

function Invoke-Clip {
    param(
        [Parameter(Mandatory = $true)][string]$Out,
        [int]$Duration = 4,
        [scriptblock]$Action = $null,
        [string]$Window = "Daybreak",
        [int]$LeadIn = 0          # 开始录制到执行动作之间的等待
    )

    $ffmpeg = Get-FFmpegPath
    $outPath = [System.IO.Path]::GetFullPath($Out)
    $outDir = Split-Path $outPath -Parent
    if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }

    $w = Get-LauncherWindow -Match $Window
    [ClipWin]::ShowWindow($w.Handle, 9) | Out-Null
    Start-Sleep -Milliseconds 200
    [ClipWin]::SetWindowPos($w.Handle, [ClipWin]::TOPMOST, 0, 0, 0, 0,
        ([ClipWin]::NOMOVE -bor [ClipWin]::NOSIZE -bor [ClipWin]::SHOW)) | Out-Null
    [ClipWin]::SetForegroundWindow($w.Handle) | Out-Null
    Start-Sleep -Milliseconds 400

    $r = New-Object ClipWin+R
    [ClipWin]::GetWindowRect($w.Handle, [ref]$r) | Out-Null
    $rx = $r.L; $ry = $r.T; $rw = ($r.Rr - $r.L); $rh = ($r.B - $r.T)

    $args = @(
        "-y", "-v", "error",
        "-f", "gdigrab", "-framerate", "30",
        "-offset_x", "$rx", "-offset_y", "$ry",
        "-video_size", "${rw}x${rh}",
        "-draw_mouse", "1",
        "-i", "desktop",
        "-t", "$Duration",
        "-c:v", "libx264", "-preset", "ultrafast", "-crf", "18",
        "-pix_fmt", "yuv420p", "-movflags", "+faststart",
        $outPath
    )

    $proc = Start-Process -FilePath $ffmpeg -ArgumentList $args -PassThru -NoNewWindow -RedirectStandardError "$env:TEMP\_clip_ff.log"

    if ($LeadIn -gt 0) { Start-Sleep -Seconds $LeadIn }
    if ($Action) {
        $sw = [System.Diagnostics.Stopwatch]::StartNew()
        & $Action
        $sw.Stop()
        # 动作期间已经占掉一部分录制时间，剩余时间补足，保证整段够 $Duration
        $left = $Duration - $LeadIn - $sw.Elapsed.TotalSeconds
        if ($left -gt 0) { Start-Sleep -Seconds ([math]::Ceiling($left)) }
    }

    $proc.WaitForExit(($Duration + 20) * 1000) | Out-Null
    if (-not $proc.HasExited) { $proc.Kill(); Start-Sleep -Milliseconds 400 }

    [ClipWin]::SetWindowPos($w.Handle, [ClipWin]::NOTOPMOST, 0, 0, 0, 0,
        ([ClipWin]::NOMOVE -bor [ClipWin]::NOSIZE)) | Out-Null

    if (Test-Path $outPath) {
        $kb = [math]::Round((Get-Item $outPath).Length / 1KB, 0)
        Write-Host ("  ✅ {0,-42} {1,5} KB  {2}s" -f (Split-Path $outPath -Leaf), $kb, $Duration)
    }
    else {
        Write-Host ("  ❌ {0}" -f (Split-Path $outPath -Leaf)) -ForegroundColor Red
        Get-Content "$env:TEMP\_clip_ff.log" -ErrorAction SilentlyContinue | Select-Object -Last 5 |
            ForEach-Object { "       $_" }
    }
}