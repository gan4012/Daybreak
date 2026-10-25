<#
.SYNOPSIS
    把 Daybreak 发布包(zip)上传到 Gitee 发行版(Release)。

.DESCRIPTION
    流程:
      1. 检查 Gitee 令牌(默认读环境变量 GITEE_TOKEN)
      2. 查询该 tag 是否已有发行版,没有就创建
      3. 用 multipart 上传 zip 作为附件
      4. 回读发行版,打印最终下载地址供校验

    为什么要脚本:Gitee 的发行版附件接口未在官方 Swagger 中公开,
    网页上传对大文件不友好,脚本化后一条命令可重复执行。

.EXAMPLE
    $env:GITEE_TOKEN = "你的令牌"
    powershell -File scripts/publish-gitee.ps1 -Tag v0.1.1 -ZipPath .\Daybreak-beta-win-x64.zip

.EXAMPLE
    # 顺带指定发行版标题与说明文件
    powershell -File scripts/publish-gitee.ps1 -Tag v0.1.2 -ZipPath .\Daybreak-beta-win-x64.zip `
        -Title "v0.1.2 内测版" -NotesFile .\release-notes.md
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$ZipPath,
    [string]$Owner = "wergansailikebai",
    [string]$Repo = "daybreak",
    [string]$Title = "",
    [string]$NotesFile = "",
    [string]$Token = $env:GITEE_TOKEN
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($Token)) {
    throw "缺少 Gitee 令牌。请先执行:`n  [Environment]::SetEnvironmentVariable('GITEE_TOKEN','你的令牌','User')`n然后重开终端,或用 -Token 参数传入。"
}

if (-not (Test-Path -LiteralPath $ZipPath)) {
    throw "找不到发布包:$ZipPath"
}

$zip = Get-Item -LiteralPath $ZipPath
$sizeMb = [math]::Round($zip.Length / 1MB, 1)
if ([string]::IsNullOrWhiteSpace($Title)) { $Title = "$Tag 内测版" }

$notes = ""
if (-not [string]::IsNullOrWhiteSpace($NotesFile) -and (Test-Path -LiteralPath $NotesFile)) {
    $notes = [System.IO.File]::ReadAllText((Resolve-Path -LiteralPath $NotesFile))
}

$api = "https://gitee.com/api/v5/repos/$Owner/$Repo"

Write-Host "==> 仓库   : $Owner/$Repo"
Write-Host "==> 标签   : $Tag"
Write-Host "==> 包体   : $($zip.Name) ($sizeMb MB)"
Write-Host ""

# ---- 1. 查发行版(按 tag) ----
$release = $null
try {
    $release = Invoke-RestMethod -Uri "$api/releases/tags/$Tag" -Method Get -TimeoutSec 30
    Write-Host "==> 发行版已存在,直接复用(id=$($release.id))"
}
catch {
    $status = $null
    if ($_.Exception.Response) { $status = [int]$_.Exception.Response.StatusCode }
    if ($status -eq 404) {
        Write-Host "==> 发行版不存在,创建中…"
    }
    else {
        throw "查询发行版失败(HTTP $status):$($_.Exception.Message)"
    }
}

# ---- 2. 创建发行版 ----
if ($null -eq $release) {
    $payload = @{
        access_token     = $Token
        tag_name         = $Tag
        name             = $Title
        body             = $notes
        target_commitish = "main"
        prerelease       = $false
    } | ConvertTo-Json -Depth 5

    try {
        $release = Invoke-RestMethod -Uri "$api/releases" -Method Post `
            -ContentType "application/json; charset=utf-8" `
            -Body ([System.Text.Encoding]::UTF8.GetBytes($payload)) -TimeoutSec 60
        Write-Host "==> 发行版已创建(id=$($release.id))"
    }
    catch {
        $detail = ""
        if ($_.Exception.Response) {
            try {
                $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
                $detail = $reader.ReadToEnd()
            }
            catch { }
        }
        throw "创建发行版失败:$($_.Exception.Message) $detail"
    }
}

$releaseId = $release.id
if (-not $releaseId) { throw "创建发行版后未拿到 id,响应异常" }

# ---- 3. 上传附件(multipart,PS 5.1 无 -Form,改用 .NET HttpClient) ----
Write-Host "==> 上传附件($sizeMb MB),大文件需要一会…"

Add-Type -AssemblyName System.Net.Http

$client = New-Object System.Net.Http.HttpClient
$client.Timeout = [TimeSpan]::FromMinutes(60)
$null = $client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Daybreak-Publish")

$fileStream = [System.IO.File]::OpenRead($zip.FullName)
try {
    $multipart = New-Object System.Net.Http.MultipartFormDataContent
    $streamContent = New-Object System.Net.Http.StreamContent($fileStream)
    $streamContent.Headers.ContentType = New-Object System.Net.Http.Headers.MediaTypeHeaderValue("application/zip")
    $multipart.Add($streamContent, "file", $zip.Name)

    $uploadUrl = "$api/releases/$releaseId/attach_files?access_token=$Token"
    $response = $client.PostAsync($uploadUrl, $multipart).GetAwaiter().GetResult()
    $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()

    if (-not $response.IsSuccessStatusCode) {
        throw "上传失败 HTTP $([int]$response.StatusCode):$body"
    }
}
finally {
    if ($fileStream) { $fileStream.Dispose() }
    if ($client) { $client.Dispose() }
}

Write-Host "==> 上传完成"
Write-Host ""

# ---- 4. 回读校验 ----
$verify = Invoke-RestMethod -Uri "$api/releases/tags/$Tag" -Method Get -TimeoutSec 30

Write-Host "=== 发行版附件清单 ==="
$matched = $null
foreach ($asset in $verify.assets) {
    $mb = if ($asset.size) { [math]::Round($asset.size / 1MB, 1) } else { 0 }
    Write-Host ("  {0}  ({1} MB)" -f $asset.name, $mb)
    Write-Host ("    {0}" -f $asset.browser_download_url)
    if ($asset.name -eq $zip.Name) { $matched = $asset }
}

Write-Host ""
if ($null -eq $matched) {
    Write-Warning "附件列表里没看到 $($zip.Name),请到网页确认"
    exit 1
}

Write-Host "=== 下一步验证 ==="
Write-Host "  1) 浏览器打开上面的 URL,确认能下载"
Write-Host "  2) 运行更新器自检:"
Write-Host "     .\publish-beta\Daybreak.Updater.exe --check"
Write-Host ""
Write-Host "✅ 完成:$Tag 已发布到 Gitee"
