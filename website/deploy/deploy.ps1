# Daybreak 官网部署脚本
#
# 服务器地址不写死在脚本里(仓库是公开的)：
#   优先取环境变量  DAYBREAK_SERVER
#   否则读同目录下的 server.txt(不进 git)
#
# 用法:
#   powershell -File website/deploy/deploy.ps1
#
# 前置:已把 deploy 公钥加到服务器的 ~/.ssh/authorized_keys

param(
    [string]$Server = "",
    [string]$KeyPath = "$env:USERPROFILE\.ssh\daybreak_deploy",
    [string]$RemotePath = "/var/www/daybreak"
)

$ErrorActionPreference = "Stop"

# ---- 解析服务器地址 ----
if (-not $Server) { $Server = $env:DAYBREAK_SERVER }
if (-not $Server) {
    $f = Join-Path $PSScriptRoot "server.txt"
    if (Test-Path $f) {
        $line = (Get-Content $f) | Where-Object { $_.Trim() } | Select-Object -First 1
        if ($line) { $Server = $line.Trim() }
    }
}
if (-not $Server) {
    Write-Host "没有服务器地址。" -ForegroundColor Red
    Write-Host "  方式1: 设置环境变量 DAYBREAK_SERVER=用户@主机"
    Write-Host "  方式2: 在 website\deploy\server.txt 里写一行 用户@主机"
    exit 1
}
if (-not (Test-Path $KeyPath)) {
    Write-Host "找不到私钥: $KeyPath" -ForegroundColor Red
    exit 1
}

# ---- 路径 ----
# $PSScriptRoot = <repo>\website\deploy  ->  站点根 = <repo>\website
$siteRoot = Split-Path $PSScriptRoot -Parent
$repoRoot = Split-Path $siteRoot -Parent
$siteName = Split-Path $siteRoot -Leaf

if (-not (Test-Path (Join-Path $siteRoot "index.html"))) {
    Write-Host "找不到 $siteRoot\index.html，路径不对" -ForegroundColor Red
    exit 1
}

Write-Host "==> 站点目录: $siteRoot"
Write-Host "==> 目标: ${Server}:${RemotePath}"

# ---- 打包（只打 website 目录）----
$tar = Join-Path $env:TEMP "daybreak-web.tar.gz"
Remove-Item $tar -Force -ErrorAction SilentlyContinue

Push-Location $repoRoot
try {
    # 排除不该公开的东西:
    #   _preview    本地审阅产物
    #   deploy      部署脚本(含服务器地址)
    #   scripts     开发脚本(字体子集、截图、审阅页生成)
    #   *.md        文档
    #   .assetsignore / _headers   Cloudflare 专用，nginx 上没用还会被当普通文件访问
    #   __pycache__ Python 缓存
    & tar -czf $tar --exclude="_preview" --exclude="__pycache__" --exclude="deploy" `
        --exclude="scripts" --exclude="*.md" `
        --exclude=".assetsignore" --exclude="_headers" `
        -C $repoRoot $siteName
} finally {
    Pop-Location
}
if (-not (Test-Path $tar)) { throw "打包失败" }

$sizeKb = [math]::Round((Get-Item $tar).Length / 1KB, 1)
if ($sizeKb -gt 20000) {
    throw "打包结果 $sizeKb KB 太大了，可能把整个仓库打进去了。请检查 tar 参数。"
}
Write-Host "==> 打包完成 $sizeKb KB"

# ---- 上传 ----
& scp -i $KeyPath -o StrictHostKeyChecking=accept-new -o ConnectTimeout=20 `
    $tar "${Server}:/tmp/daybreak-web.tar.gz"
if ($LASTEXITCODE -ne 0) { throw "上传失败" }
Write-Host "==> 上传完成"

# ---- 远端解包替换 ----
# 用单引号 here-string，避免 PowerShell 把 $ 变量当成自己的
$bashTemplate = @'
#!/bin/bash
set -e
SITE="__SITE__"
REMOTE="__REMOTE__"
sudo mkdir -p "$(dirname "$REMOTE")"
sudo rm -rf /tmp/daybreak-site "${REMOTE}.new"
sudo tar -xzf /tmp/daybreak-web.tar.gz -C /tmp
sudo mv "/tmp/$SITE" "${REMOTE}.new"
sudo rm -rf "$REMOTE"
sudo mv "${REMOTE}.new" "$REMOTE"
sudo chown -R www-data:www-data "$REMOTE"
sudo find "$REMOTE" -type d -exec chmod 755 {} \;
sudo find "$REMOTE" -type f -exec chmod 644 {} \;
rm -f /tmp/daybreak-web.tar.gz
echo "--- 自测 ---"
curl -sS -o /dev/null -w "index: HTTP %{http_code}\n" http://127.0.0.1/
curl -sS -o /dev/null -w "css:   HTTP %{http_code}\n" http://127.0.0.1/assets/style.css
'@

$bash = $bashTemplate.Replace("__SITE__", $siteName).Replace("__REMOTE__", $RemotePath)
# 关键:这个 .ps1 是 CRLF 行尾(PS 5.1 要求)，但 bash 不认 \r，
# 不剥掉会让远端报 "cannot stat '$/var/www/...'" 这类诡异错误。
$bash = $bash -replace "`r", ""
$b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($bash))

& ssh -i $KeyPath -o StrictHostKeyChecking=accept-new $Server "echo $b64 | base64 -d | bash"
if ($LASTEXITCODE -ne 0) { throw "远端部署失败" }

$hostOnly = ($Server -split "@")[-1]
Write-Host ""
Write-Host "==> 部署完成: http://$hostOnly/" -ForegroundColor Green
