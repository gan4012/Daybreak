# 配音 + 字幕一键生成
#
# 输入一个纯文本脚本(一行一句旁白)，输出:
#   配音.mp3    —— 按顺序拼接好的整条配音，句间有停顿
#   字幕.srt    —— 和配音严格对齐的字幕，带 BOM(剪映能正确识别)
#
# 用法:
#   powershell -File docs/video/tools/tts.ps1 -ScriptFile docs/video/script-抖音.txt
#
#   # 换音色 / 改语速
#   powershell -File docs/video/tools/tts.ps1 -ScriptFile xxx.txt -Voice zh-CN-YunjianNeural -Rate "+20%"
#
# 为什么要合并成一条:
#   剪映里把一条 mp3 拖到时间轴，再把 srt 作为字幕导入，两边天然对齐。
#   如果是一堆碎 mp3，手工摆位置能耗掉半小时。

param(
    [Parameter(Mandatory = $true)][string]$ScriptFile,
    [string]$Voice = "zh-CN-YunxiNeural",
    [string]$Rate = "+10%",
    [double]$GapSeconds = 0.45,
    [string]$OutDir = "",
    [string]$Python = "C:\Users\32109\AppData\Local\Programs\Python\Python312\python.exe"
)

$ErrorActionPreference = "Stop"

# ---- 找 ffmpeg(winget 装的，可能不在当前会话 PATH 里)----
function Get-FFmpeg {
    $cmd = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $cand = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Recurse -Filter "ffmpeg.exe" -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($cand) { return $cand.FullName }
    throw "找不到 ffmpeg。请先执行: winget install --id Gyan.FFmpeg -e"
}

function Get-FFprobe {
    $ff = Get-FFmpeg
    $probe = Join-Path (Split-Path $ff -Parent) "ffprobe.exe"
    if (Test-Path $probe) { return $probe }
    throw "找不到 ffprobe"
}

$ffmpeg = Get-FFmpeg
$ffprobe = Get-FFprobe

# ---- 校验输入 ----
$scriptPath = (Resolve-Path $ScriptFile).Path
if (-not $OutDir) {
    $OutDir = Join-Path (Split-Path $scriptPath -Parent) "配音输出"
}
# 必须转成绝对路径:ffmpeg 的 concat 列表是按「列表文件所在目录」解析相对路径的，
# 而不是按当前工作目录。传相对路径的 -OutDir 会让拼接步骤找不到分段文件。
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

# 读脚本:跳过空行和以 # 开头的注释行
$lines = Get-Content $scriptPath -Encoding UTF8 |
    ForEach-Object { $_.Trim() } |
    Where-Object { $_ -ne "" -and -not $_.StartsWith("#") }

if ($lines.Count -eq 0) { throw "脚本文件里没有可用的句子: $scriptPath" }

Write-Host "==> 音色: $Voice   语速: $Rate   停顿: ${GapSeconds}s" -ForegroundColor Cyan
Write-Host "==> 共 $($lines.Count) 句" -ForegroundColor Cyan

# ---- 逐句生成 ----
$segDir = Join-Path $OutDir "_segments"
if (Test-Path $segDir) { Remove-Item $segDir -Recurse -Force }
New-Item -ItemType Directory -Path $segDir -Force | Out-Null

$segments = @()
for ($i = 0; $i -lt $lines.Count; $i++) {
    $n = "{0:D2}" -f ($i + 1)
    $mp3 = Join-Path $segDir "$n.mp3"
    $text = $lines[$i]

    & $Python -m edge_tts --voice $Voice --rate=$Rate --text $text --write-media $mp3 2>&1 | Out-Null
    if (-not (Test-Path $mp3)) { throw "第 $($i+1) 句配音失败: $text" }

    $dur = [double](& $ffprobe -v error -show_entries format=duration -of csv=p=0 $mp3 2>&1 | Select-Object -First 1)
    $segments += [pscustomobject]@{ Index = $i + 1; File = $mp3; Text = $text; Duration = $dur }
    Write-Host ("    {0}. {1,6:N2}s  {2}" -f ($i + 1), $dur, $(if ($text.Length -gt 30) { $text.Substring(0, 30) + "…" } else { $text }))
}

# ---- 拼成一条(句间插静音)----
Write-Host "==> 拼接配音…" -ForegroundColor Cyan
$silence = Join-Path $segDir "_silence.mp3"
& $ffmpeg -y -v error -f lavfi -i "anullsrc=r=24000:cl=mono" -t $GapSeconds -c:a libmp3lame -q:a 9 $silence 2>&1 | Out-Null

$listFile = Join-Path $segDir "_list.txt"
$listLines = @()
foreach ($s in $segments) {
    $listLines += "file '$($s.File -replace '\\','/')'"
    $listLines += "file '$($silence -replace '\\','/')'"
}
# 末尾那段静音去掉
$listLines = $listLines[0..($listLines.Count - 2)]
[System.IO.File]::WriteAllLines($listFile, $listLines, (New-Object System.Text.UTF8Encoding($false)))

$voiceMp3 = Join-Path $OutDir "配音.mp3"
# edge-tts 出来的是 24kHz 单声道，剪映能读，但 48kHz 立体声才是视频剪辑的通用规格，
# 否则后面和背景音乐混音时采样率对不上。
& $ffmpeg -y -v error -f concat -safe 0 -i $listFile `
    -ar 48000 -ac 2 -c:a libmp3lame -q:a 3 $voiceMp3 2>&1 | Out-Null
if (-not (Test-Path $voiceMp3)) { throw "拼接失败" }

$totalDur = [double](& $ffprobe -v error -show_entries format=duration -of csv=p=0 $voiceMp3 2>&1 | Select-Object -First 1)

# ---- 生成字幕(按实际时长累加，和配音严丝合缝)----
Write-Host "==> 生成字幕…" -ForegroundColor Cyan
function Format-Time([double]$sec) {
    $ts = [TimeSpan]::FromSeconds($sec)
    return "{0:D2}:{1:D2}:{2:D2},{3:D3}" -f $ts.Hours, $ts.Minutes, $ts.Seconds, $ts.Milliseconds
}

$srt = New-Object System.Text.StringBuilder
$cursor = 0.0
for ($i = 0; $i -lt $segments.Count; $i++) {
    $s = $segments[$i]
    $start = $cursor
    $end = $cursor + $s.Duration
    [void]$srt.AppendLine("$($i + 1)")
    [void]$srt.AppendLine("$(Format-Time $start) --> $(Format-Time $end)")
    [void]$srt.AppendLine($s.Text)
    [void]$srt.AppendLine("")
    $cursor = $end + $GapSeconds
}

# SRT 必须带 BOM，否则 Windows 上的剪映会按 ANSI 解码成乱码
$srtPath = Join-Path $OutDir "字幕.srt"
[System.IO.File]::WriteAllText($srtPath, $srt.ToString(), (New-Object System.Text.UTF8Encoding($true)))

# ---- 清理 ----
Remove-Item $segDir -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "==> 完成" -ForegroundColor Green
Write-Host "    配音: $voiceMp3  ($([math]::Round((Get-Item $voiceMp3).Length/1KB,1)) KB, ${totalDur}s)"
Write-Host "    字幕: $srtPath"
Write-Host ""
Write-Host "==> 下一步:剪映里导入这两个文件" -ForegroundColor Cyan
Write-Host "    1. 音频 → 导入 → 配音.mp3 → 拖到时间轴最左端"
Write-Host "    2. 文本 → 智能字幕 → 导入字幕 → 字幕.srt"
Write-Host "    3. 按配音节奏摆画面"