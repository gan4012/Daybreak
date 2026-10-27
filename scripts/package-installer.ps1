<#
.SYNOPSIS
    用 Inno Setup 把 publish-beta 目录打成 Windows 安装包。

.DESCRIPTION
    产物:dist\Daybreak-Setup-<版本>.exe

    前置:先跑 scripts/package.ps1 生成 publish-beta/，或用 -SkipBuild 复用现有产物。

    安装包特性(详见 scripts/daybreak.iss 的注释):
      · 装到 %LOCALAPPDATA%\Daybreak，全程不需要管理员权限
      · 开始菜单快捷方式默认勾选，桌面默认不勾
      · 卸载只删安装目录，不动 %APPDATA% 里的用户数据
      · 装完之后应用内自更新照常工作(因为目录可写)

.EXAMPLE
    powershell -File scripts/package-installer.ps1
    powershell -File scripts/package-installer.ps1 -SkipBuild
#>
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    # 只打印将要执行的命令，不真的打包
    [switch]$DryRun,
    # 必须打出安装包(CI 用)。找不到 Inno Setup 时直接失败而不是跳过，
    # 否则 CI 会绿着跑完、Release 照发，但产物里没有安装包。
    [switch]$RequireInstaller
)

$ErrorActionPreference = "Stop"

$root = Split-Path $PSScriptRoot -Parent
$issPath = Join-Path $PSScriptRoot "daybreak.iss"
$stagingDir = Join-Path $root "publish-beta"
$distDir = Join-Path $root "dist"

# ---- 1. 读版本号 ----
# 必须用 ReadAllText + 正则，不能用 [xml](Get-Content -Raw):
# PS 5.1 会把 UTF-8 的 props 文件按 ANSI 读，中文注释变乱码导致 XML 解析失败。
$propsPath = Join-Path $root "Directory.Build.props"
if (-not (Test-Path $propsPath)) { throw "找不到 Directory.Build.props" }
$propsText = [System.IO.File]::ReadAllText($propsPath, [System.Text.Encoding]::UTF8)
$m = [regex]::Match($propsText, '<Version>([^<]+)</Version>')
if (-not $m.Success) { throw "Directory.Build.props 里没有 <Version>" }
$version = $m.Groups[1].Value.Trim()
Write-Host "==> 版本: $version" -ForegroundColor Cyan

# ---- 2. 前置检查 ----
if (-not $SkipBuild) {
    Write-Host "==> 先跑 scripts/package.ps1 生成 publish-beta" -ForegroundColor Cyan
    & powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "package.ps1")
    if ($LASTEXITCODE -ne 0) { throw "package.ps1 失败" }
}

$launcherExe = Join-Path $stagingDir "Daybreak.Launcher.exe"
if (-not (Test-Path $launcherExe)) {
    Write-Host "找不到 $launcherExe" -ForegroundColor Red
    Write-Host "  请先跑 scripts/package.ps1，或去掉 -SkipBuild 参数。" -ForegroundColor Red
    exit 1
}

# 联机组件必须齐全，否则装完联机不可用(这个坑在打包脚本里踩过一次)
foreach ($f in @("easytier\easytier-core.exe", "easytier\relay.txt")) {
    $p = Join-Path $stagingDir $f
    if (-not (Test-Path $p)) {
        Write-Host "publish-beta 里缺 $f，打出来的安装包联机会不可用。" -ForegroundColor Red
            Write-Host "  请先跑 scripts/package.ps1。" -ForegroundColor Red
            exit 1
    }
}
Write-Host "==> publish-beta 检查通过(主程序 + 联机组件齐全)" -ForegroundColor Green

# ---- 3. 定位 ISCC.exe ----
$iscc = $null
$cmd = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
if ($cmd) {
    $iscc = $cmd.Source
}
else {
    # 常见安装位置
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 5\ISCC.exe")
    )
    foreach ($c in $candidates) {
        if ($c -and (Test-Path $c)) { $iscc = $c; break }
    }
}

if (-not $iscc) {
    if ($RequireInstaller) {
        Write-Host ""
        Write-Host "找不到 Inno Setup 的 ISCC.exe，但指定了 -RequireInstaller。" -ForegroundColor Red
        Write-Host "  安装: winget install --id JRSoftware.InnoSetup -e" -ForegroundColor Red
        exit 2
    }
    Write-Host ""
    Write-Host "没有找到 Inno Setup 的 ISCC.exe，跳过安装包生成。" -ForegroundColor Yellow
    Write-Host "  装一下(二选一):" -ForegroundColor Yellow
    Write-Host "    winget install --id JRSoftware.InnoSetup -e"
    Write-Host "    或者去 https://jrsoftware.org/isdl.php 下载"
    Write-Host ""
    Write-Host "  绿色包(scripts/package.ps1 产出的 zip)不受影响，照常可用。" -ForegroundColor Yellow
    exit 0   # 不算失败:没装 Inno Setup 的人也应该能打 zip
}
Write-Host "==> ISCC: $iscc" -ForegroundColor Cyan

if (-not (Test-Path $distDir)) { New-Item -ItemType Directory -Path $distDir -Force | Out-Null }

# ---- 4. 打包 ----
$isccArgs = @("/DMyAppVersion=$version", "`"$issPath`"")
Write-Host "==> 执行: `"$iscc`" $($isccArgs -join ' ')" -ForegroundColor Cyan

if ($DryRun) {
    Write-Host "  (-DryRun:不真的打包)" -ForegroundColor Yellow
    exit 0
}

$output = & $iscc $isccArgs 2>&1
$exitCode = $LASTEXITCODE
# ISCC 的输出是 GBK，转一下免得控制台乱码
$output | ForEach-Object { Write-Host "  $_" }

if ($exitCode -ne 0) {
    Write-Host ""
    Write-Host "ISCC 打包失败，退出码 $exitCode" -ForegroundColor Red
    # 透传 ISCC 的真实退出码，不要用 throw ——
    # throw 在 powershell -File 下统一变成退出码 1，调用方区分不了
    # "没装 Inno Setup"(0) 和 "ISCC 真失败"(非 0)。
    exit $exitCode
}

# ---- 5. 结果 ----
$setupExe = Join-Path $distDir "Daybreak-Setup-$version.exe"
if (-not (Test-Path $setupExe)) {
    Write-Host "ISCC 报告成功，但找不到产物 $setupExe" -ForegroundColor Red
    exit 1
}

$sizeMb = [math]::Round((Get-Item $setupExe).Length / 1MB, 1)
$sha = (Get-FileHash $setupExe -Algorithm SHA256).Hash
$shaPath = "$setupExe.sha256"
[System.IO.File]::WriteAllText($shaPath, "$sha  Daybreak-Setup-$version.exe`r`n", (New-Object System.Text.UTF8Encoding($false)))

Write-Host ""
Write-Host "==> 安装包完成: $setupExe" -ForegroundColor Green
Write-Host "    $sizeMb MB"
Write-Host "    SHA256: $sha"
Write-Host "    校验文件: $shaPath"
Write-Host ""
Write-Host "==> 说明" -ForegroundColor Cyan
Write-Host "    装在 %LOCALAPPDATA%\Daybreak，不需要管理员权限"
Write-Host "    开始菜单快捷方式默认创建，桌面默认不建"
Write-Host "    卸载不会删 %APPDATA%\Daybreak 里的配置和账号"