# Daybreak 内测包打包脚本
# 用法: powershell -File scripts/package.ps1 [-SelfContained]
#   默认: 自包含单文件(无需安装 .NET,包体较大 ~100MB)
#   -SelfContained:$false: 依赖 .NET Desktop Runtime 10(包体小,需用户装运行时)

param(
    [switch]$SelfContained = $true
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot | Split-Path
Set-Location $root

$outDir = Join-Path $root "publish-beta"
$zipName = "Daybreak-内测包-win-x64.zip"

if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }

Write-Host "==> dotnet publish (self-contained: $SelfContained)" -ForegroundColor Cyan
$singleFile = if ($SelfContained) { "true" } else { "false" }
dotnet publish Daybreak.Launcher -c Release -r win-x64 `
    --self-contained:$SelfContained `
    -p:PublishSingleFile=$singleFile `
    -p:IncludeNativeLibrariesForSelfExtract=$singleFile `
    -p:EnableCompressionInSingleFile=true `
    -o $outDir

if ($LASTEXITCODE -ne 0) { throw "publish 失败" }

# 独立更新器:与主程序同目录,包内需带 Daybreak.Updater.exe 否则「检查更新」失效
Write-Host "==> dotnet publish 更新器" -ForegroundColor Cyan
$updaterTmp = Join-Path $root "updater-tmp"
if (Test-Path $updaterTmp) { Remove-Item $updaterTmp -Recurse -Force }
dotnet publish Daybreak.Updater -c Release -r win-x64 `
    --self-contained:$SelfContained `
    -p:PublishSingleFile=$singleFile `
    -p:IncludeNativeLibrariesForSelfExtract=$singleFile `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishTrimmed=$SelfContained `
    -o $updaterTmp

if ($LASTEXITCODE -ne 0) { throw "更新器 publish 失败" }

Write-Host "==> 合并更新器到包目录" -ForegroundColor Cyan
Get-ChildItem -Path $updaterTmp -Filter "Daybreak.Updater.*" |
    Where-Object { $_.Extension -in ".exe", ".pdb" } |
    Copy-Item -Destination $outDir -Force
Remove-Item $updaterTmp -Recurse -Force

if (-not (Test-Path (Join-Path $outDir "Daybreak.Updater.exe"))) {
    throw "Daybreak.Updater.exe 未生成到包目录"
}

Write-Host "==> 写入内测说明" -ForegroundColor Cyan
@'

========================================
  Daybreak 破晓启动器 · 内测版
========================================

【启动方式】
双击 Daybreak.Launcher.exe

【系统要求】
Windows 10 1809 及以上(64 位)

【首次使用】
1. 首页可保存离线账号(填玩家名 → 保存账号 → 开始游戏)
2. 点「下载游戏」页安装一个游戏版本(建议先装正式版)
3. 首次启动游戏会自动下载:
   - 匹配的 Java 运行时(自动选择版本)
   - 游戏资源文件(可能数百 MB,取决于版本)
   下载进度显示在进度条与日志区,完成后自动拉起游戏

【已支持的登录方式】
- 离线登录(单机与测试)
- 微软正版登录(设备码授权)
- 外置登录(Yggdrasil 服务器,如 LittleSkin)

【已知限制】
- 仅支持 Windows 10 1809+
- 正版登录需要拥有 Minecraft: Java Edition 正版账号
- 首次启动较慢属正常现象(下载资源)

【反馈】
遇到问题请带上 %APPDATA%\Daybreak\logs 目录下的日志反馈。
'@ | Out-File -FilePath (Join-Path $outDir "使用说明.txt") -Encoding utf8

Write-Host "==> 压缩打包" -ForegroundColor Cyan
$zipPath = Join-Path $root $zipName
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $outDir "*") -DestinationPath $zipPath

$size = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host "==> 完成: $zipName ($size MB)" -ForegroundColor Green
