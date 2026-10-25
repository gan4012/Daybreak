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
# 包名必须全 ASCII:GitHub Release 上传会把非 ASCII 字符替换成 '.'(实测「内测包」→「.」),
# 导致自更新器按文件名匹配资产失败
$zipName = "Daybreak-beta-win-x64.zip"

# ---- EasyTier 联机组件 ----
# 二进制不进 git(约 24MB),新克隆的仓库里 third_party/easytier 只有许可证文本。
# 这里负责在打包前补齐,否则打出来的包缺失联机功能。
$etDir = Join-Path $root "third_party\easytier"
$etCore = Join-Path $etDir "easytier-core.exe"
$etVersion = "v2.6.4"

if (-not (Test-Path $etCore)) {
    Write-Host "==> 缺少 EasyTier 组件,自动下载 $etVersion" -ForegroundColor Yellow
    New-Item -ItemType Directory -Path $etDir -Force | Out-Null

    $zipUrl = "https://github.com/EasyTier/EasyTier/releases/download/$etVersion/easytier-windows-x86_64-$etVersion.zip"
    $tmpZip = Join-Path $env:TEMP "easytier-$etVersion.zip"
    $tmpDir = Join-Path $env:TEMP "easytier-extract-$etVersion"

    try {
        # GitHub 在国内不稳,失败时走镜像
        $mirrors = @($zipUrl, "https://gh-proxy.com/$zipUrl", "https://ghfast.top/$zipUrl")
        $downloaded = $false
        foreach ($u in $mirrors) {
            try {
                Invoke-WebRequest -Uri $u -OutFile $tmpZip -TimeoutSec 180 -UseBasicParsing `
                    -Headers @{ "User-Agent" = "Daybreak-Build" }
                if ((Get-Item $tmpZip).Length -gt 10MB) { $downloaded = $true; break }
            } catch { }
        }
        if (-not $downloaded) { throw "所有下载源均失败" }

        if (Test-Path $tmpDir) { Remove-Item $tmpDir -Recurse -Force }
        Expand-Archive -Path $tmpZip -DestinationPath $tmpDir -Force

        $srcDir = Get-ChildItem $tmpDir -Recurse -Filter "easytier-core.exe" | Select-Object -First 1 -ExpandProperty DirectoryName
        foreach ($f in @("easytier-core.exe", "wintun.dll", "WinDivert64.sys", "Packet.dll")) {
            $src = Join-Path $srcDir $f
            if (Test-Path $src) { Copy-Item $src $etDir -Force }
        }
        Write-Host "    EasyTier 组件已就位" -ForegroundColor Green
    } catch {
        throw "EasyTier 组件下载失败:$_`n请手动把 easytier-core.exe / wintun.dll / WinDivert64.sys / Packet.dll 放到 third_party\easytier\"
    } finally {
        Remove-Item $tmpZip -Force -ErrorAction SilentlyContinue
        Remove-Item $tmpDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

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

# ---- 显式复制 EasyTier 组件 ----
# 不依赖 csproj 的 Content:单文件发布时 .dll 会被当成 native 库打进单文件、
# .exe/.sys 也未必按 Content 复制,实测 publish 目录里只剩 .txt。
# 这里直接从源目录复制，结果最确定。
$etOut = Join-Path $outDir "easytier"
if (Test-Path $etDir) {
    New-Item -ItemType Directory -Path $etOut -Force | Out-Null
    foreach ($f in @("easytier-core.exe", "wintun.dll", "WinDivert64.sys", "Packet.dll",
                     "LICENSE-EasyTier-LGPL-3.0.txt", "README-EasyTier.txt", "relay.txt")) {
        $src = Join-Path $etDir $f
        if (Test-Path $src) { Copy-Item $src $etOut -Force }
    }
    $etFiles = (Get-ChildItem $etOut -File | Measure-Object).Count
    Write-Host "    EasyTier 组件已复制($etFiles 个文件)" -ForegroundColor Green
    if (-not (Test-Path (Join-Path $etOut "easytier-core.exe"))) {
        throw "EasyTier 组件复制失败:publish 目录里没有 easytier-core.exe"
    }
    # relay.txt 含自建节点地址,不进 git,但必须进包,否则联机功能用不了
    if (-not (Test-Path (Join-Path $etOut "relay.txt"))) {
        throw "缺少 third_party\easytier\relay.txt(内容为一行 host:port)。该文件不进 git,需在本地创建后再打包。"
    }
} else {
    throw "找不到 $etDir"
}

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

【目录说明】
Daybreak.Launcher.exe   主程序
Daybreak.Updater.exe    更新器(主程序会自动调用)
easytier\               联机组件,**请勿删除**,删了联机功能就用不了
logs\                   运行日志,反馈问题时请一并提供

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

【联机:自己开服】
「联机」→「我的服务端」→「新建服务端」,选版本与类型
(Vanilla / Fabric / Paper / Forge),首次启动会下载服务端核心。
开好后在「控制台」可以发 op / list 等命令。

【联机:和好友一起玩(不需要公网 IP)】
1. 你点「联机」→「好友联机」→「创建房间」
   首次会提示需要管理员权限(创建虚拟网卡),点「是」重启为管理员
2. 拿到 6 位房间号,发给好友
3. 好友在他的启动器里输入房间号 → 「加入房间」
4. 你在「我的服务端」里启动一个服务端
5. 双方点「启动游戏并进服」,游戏会直接连进去

注意:
- 双方的游戏版本与 Mod 必须【完全一致】,否则连不上
- 房间号相当于弱密码,建议只告诉自己人;直播/录屏时注意遮挡
- 出问题请把联机页里的「联机日志」一起发来

【已知限制】
- 仅支持 Windows 10 1809+
- 正版登录需要拥有 Minecraft: Java Edition 正版账号
- 首次启动较慢属正常现象(下载资源)
- 好友联机需要管理员权限;不用联机则不需要
- NeoForge 在国内网络下可能装不上(上游仓库直连超时),可先配代理

【反馈】
遇到问题请带上 %APPDATA%\Daybreak\logs 目录下的日志反馈,
联机问题请一并附上联机页的「联机日志」。
'@ | Out-File -FilePath (Join-Path $outDir "使用说明.txt") -Encoding utf8

Write-Host "==> 压缩打包" -ForegroundColor Cyan
$zipPath = Join-Path $root $zipName
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $outDir "*") -DestinationPath $zipPath

$size = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host "==> 完成: $zipName ($size MB)" -ForegroundColor Green

# ---- 同步到干净的发行目录 ----
# 源码目录里 bin/obj/publish/zip 混在一起很难找"能双击的那个 exe"。
# 这里把发行文件额外同步一份到 $ReleaseDir,桌面快捷方式指向那里,
# 用户日常只需要认这一个目录。
$releaseDir = "D:\Daybreak"

Write-Host "==> 同步发行目录 $releaseDir" -ForegroundColor Cyan
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null

# 只放运行需要的:主程序、更新器、说明。不带 pdb,避免用户困惑
# 联机组件目录也要同步(否则发行目录里的程序找不到 easytier-core)
    $etSrc = Join-Path $outDir "easytier"
    $etDst = Join-Path $releaseDir "easytier"
    if (Test-Path $etSrc) {
        if (Test-Path $etDst) { Remove-Item $etDst -Recurse -Force }
        Copy-Item $etSrc $etDst -Recurse -Force
    }

    foreach ($name in @("Daybreak.Launcher.exe", "Daybreak.Updater.exe", "使用说明.txt")) {
    $src = Join-Path $outDir $name
    if (Test-Path $src) {
        Copy-Item $src $releaseDir -Force
    }
}

# 清掉发行目录里的陈旧 pdb(以前复制过的话)
Get-ChildItem $releaseDir -Filter "*.pdb" -ErrorAction SilentlyContinue | Remove-Item -Force

$relSize = [math]::Round(((Get-ChildItem $releaseDir -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host "==> 发行目录已更新: $releaseDir ($relSize MB)" -ForegroundColor Green
Write-Host "    可直接双击: $releaseDir\Daybreak.Launcher.exe"
