# Daybreak 网盘分发包
#
# 与 package.ps1 的区别:
#   package.ps1        -> Daybreak-beta-win-x64.zip(给 GitHub Release/自更新器用，文件名固定)
#   package-netdisk.ps1 -> Daybreak-v<版本>-win-x64.zip(给网盘用)
#
# 网盘包的三个处理:
#   1. 不带 .pdb —— 调试符号对用户没用，还会暴露本地源码路径
#   2. 顶层带一个版本号文件夹 —— 解压出来是整齐的一个目录，不是一堆散文件
#   3. 附 SHA256 校验文件 —— 网盘下载容易损坏，用户能自己核对
#
# 用法:
#   powershell -File scripts/package-netdisk.ps1
#
# 前置:先跑过 package.ps1(它负责 publish 与 easytier 组件准备)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot | Split-Path
Set-Location $root

# ---- 读版本号 ----
$propsFile = Join-Path $root "Directory.Build.props"
# 用正则取版本号。不要用 Get-Content + [xml]:
# PS 5.1 的 Get-Content 默认按 ANSI 读，UTF-8 中文会变乱码，XML 直接解析失败。
$rawProps = [System.IO.File]::ReadAllText($propsFile, [System.Text.Encoding]::UTF8)
if ($rawProps -notmatch '<Version>\s*([^<\s]+)\s*</Version>') {
    throw "Directory.Build.props 里找不到 <Version>"
}
$version = $Matches[1]
if (-not $version) { throw "Directory.Build.props 里没有 <Version>" }
Write-Host "==> 版本: $version" -ForegroundColor Cyan

$outDir = Join-Path $root "publish-beta"
if (-not (Test-Path $outDir)) {
    throw "找不到 publish-beta。请先跑 scripts/package.ps1"
}

$folderName = "Daybreak-v$version-win-x64"
$zipName = "$folderName.zip"
$zipPath = Join-Path $root $zipName

# ---- 组装临时目录 ----
$staging = Join-Path $env:TEMP "daybreak-netdisk-staging"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
$stageRoot = Join-Path $staging $folderName
New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null

Write-Host "==> 复制文件(不含 .pdb)" -ForegroundColor Cyan
$copied = 0
Get-ChildItem $outDir -Recurse -File | Where-Object {
    $_.Extension -ne ".pdb" -and $_.Name -ne ".gitkeep"
} | ForEach-Object {
    $rel = $_.FullName.Substring($outDir.Length).TrimStart('\')
    $dest = Join-Path $stageRoot $rel
    $destDir = Split-Path $dest -Parent
    if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }
    Copy-Item $_.FullName $dest -Force
    $script:copied++
}
Write-Host "    复制了 $copied 个文件"

# ---- 必要的完整性检查 ----
$must = @("Daybreak.Launcher.exe", "easytier\easytier-core.exe", "easytier\relay.txt")
foreach ($m in $must) {
    if (-not (Test-Path (Join-Path $stageRoot $m))) {
        throw "包内缺少必要文件: $m"
    }
}
Write-Host "    必要文件检查通过" -ForegroundColor Green

# ---- 额外附一份校验说明 ----
$sha = (Get-FileHash (Join-Path $stageRoot "Daybreak.Launcher.exe") -Algorithm SHA256).Hash
$notePath = Join-Path $stageRoot "文件校验.txt"
@"
Daybreak 破晓启动器 v$version
========================================

如果你担心下载的文件不完整，可以核对下面这个值。

主程序 Daybreak.Launcher.exe 的 SHA256:
$sha

核对方法（Windows 自带，不用装任何东西）:
1. 在解压后的目录里，按住 Shift + 鼠标右键 → 「在此处打开 PowerShell 窗口」
2. 执行:
   Get-FileHash .\Daybreak.Launcher.exe -Algorithm SHA256
3. 把输出里的 Hash 和上面的值对比，一致就是完好的

完整的安装与使用说明见「使用说明.txt」。
"@ | Out-File -FilePath $notePath -Encoding utf8

# ---- 压缩 ----
Write-Host "==> 压缩" -ForegroundColor Cyan
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $staging, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
# 第 4 个参数必须是 $false:staging 目录里已经有一层版本文件夹，
# 传 $true 会把临时目录名 daybreak-netdisk-staging 也塞进 zip 根节点。

# ---- 外层校验文件(方便网盘旁边一起放) ----
$zipHash = (Get-FileHash $zipPath -Algorithm SHA256).Hash
$shaLine = "$zipHash  $zipName"
$shaPath = Join-Path $root "$zipName.sha256"
$shaLine | Out-File -FilePath $shaPath -Encoding ascii -NoNewline

# ---- 清理 ----
Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue

$sizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host ""
Write-Host "==> 完成: $zipName ($sizeMb MB)" -ForegroundColor Green
Write-Host "    SHA256: $zipHash"
Write-Host "    校验文件: $(Split-Path $shaPath -Leaf)"
Write-Host ""

# ---- 列出包内容摘要 ----
Write-Host "==> 包内结构" -ForegroundColor Cyan
$zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
$zip.Entries |
    Sort-Object FullName |
    Select-Object @{n = '路径'; e = { $_.FullName } },
                  @{n = '大小'; e = { if ($_.Length -gt 0) { "{0:N1} KB" -f ($_.Length / 1KB) } else { "" } } } |
    Format-Table -AutoSize
$zip.Dispose()
