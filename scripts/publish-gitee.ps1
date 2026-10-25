<#
.SYNOPSIS
    把 Daybreak 发布包(zip)上传到 Gitee 发行版(Release)。

.DESCRIPTION
    流程:
      1. 检查 Gitee 令牌(默认读环境变量 GITEE_TOKEN)
      2. 查询该 tag 是否已有发行版:没有就创建,已有则更新标题/说明(幂等,可重复执行)
      3. 用 multipart 上传 zip 作为附件(同名附件已存在则跳过)
      4. 回读发行版,打印最终下载地址供校验

    两个必须注意的 Gitee 坑(已在本脚本内处理):
      - 发行版不存在时 GET /releases/tags/{tag} 返回 HTTP 200 + 正文 "null",不是 404
      - JSON 里的中文若直接发 UTF-8 字节,Gitee 会按 Latin-1 解码成乱码;
        必须转成 \uXXXX 纯 ASCII 转义

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
    [switch]$ForceUpload,
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

function ConvertTo-AsciiJson {
    <#
      把字符串里的非 ASCII 字符转成 \uXXXX,产出纯 ASCII 的 JSON 文本。
      Gitee 的 JSON 解析会把 UTF-8 字节按 Latin-1 处理,直接发中文会乱码。
    #>
    param([Parameter(Mandatory = $true)][string]$Text)

    $sb = New-Object System.Text.StringBuilder
    foreach ($ch in $Text.ToCharArray()) {
        $code = [int]$ch
        if ($code -gt 127) {
            $null = $sb.Append(("\u{0:x4}" -f $code))
        }
        else {
            $null = $sb.Append($ch)
        }
    }
    return $sb.ToString()
}

function New-ReleasePayload {
    param([string]$Name, [string]$Body, [switch]$ForCreate)

    $pairs = [ordered]@{
        access_token = $Token
        tag_name     = $Tag
        name         = $Name
        body         = $Body
    }
    if ($ForCreate) {
        $pairs["target_commitish"] = "main"
        $pairs["prerelease"] = $false
    }

    $json = ($pairs | ConvertTo-Json -Depth 5 -Compress)
    return ConvertTo-AsciiJson -Text $json
}

Write-Host "==> 仓库   : $Owner/$Repo"
Write-Host "==> 标签   : $Tag"
Write-Host "==> 包体   : $($zip.Name) ($sizeMb MB)"
Write-Host ""

# ---- 1. 查发行版(按 tag) ----
# Gitee 在不存在时返回 200 + "null",所以必须校验 id 而不是看异常
$release = $null
try {
    $existing = Invoke-RestMethod -Uri "$api/releases/tags/$Tag" -Method Get -TimeoutSec 30
    if ($existing -and $existing.id) { $release = $existing }
}
catch {
    # 网络/权限问题也继续尝试创建,由创建步骤报出真实错误
}

if ($release) {
    Write-Host "==> 发行版已存在(id=$($release.id)),更新标题与说明"

    $patchJson = New-ReleasePayload -Name $Title -Body $notes
    try {
        $null = Invoke-RestMethod -Uri "$api/releases/$($release.id)" -Method Patch `
            -ContentType "application/json; charset=utf-8" `
            -Body ([System.Text.Encoding]::ASCII.GetBytes($patchJson)) -TimeoutSec 60
        Write-Host "==> 标题/说明已更新"
    }
    catch {
        Write-Warning "更新标题失败(不影响附件):$($_.Exception.Message)"
    }
}
else {
    Write-Host "==> 创建发行版…"
    $createJson = New-ReleasePayload -Name $Title -Body $notes -ForCreate

    try {
        $release = Invoke-RestMethod -Uri "$api/releases" -Method Post `
            -ContentType "application/json; charset=utf-8" `
            -Body ([System.Text.Encoding]::ASCII.GetBytes($createJson)) -TimeoutSec 60
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

    if (-not ($release -and $release.id)) {
        $raw = if ($release) { $release | ConvertTo-Json -Depth 5 } else { "(空)" }
        throw "创建发行版返回内容异常,未拿到 id。原始响应:`n$raw"
    }
    Write-Host "==> 发行版已创建(id=$($release.id))"
}

$releaseId = $release.id

# ---- 2. 检查同附件是否已上传 ----
$alreadyHas = $false
try {
    $check = Invoke-RestMethod -Uri "$api/releases/tags/$Tag" -Method Get -TimeoutSec 30
    if ($check -and $check.assets) {
        $alreadyHas = [bool]($check.assets | Where-Object { $_.name -eq $zip.Name })
    }
}
catch { }

if ($alreadyHas -and -not $ForceUpload) {
    Write-Host "==> 附件 $($zip.Name) 已存在,跳过上传(需要覆盖请加 -ForceUpload)"
}
else {
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
        Write-Host "==> 上传完成"
    }
    finally {
        if ($fileStream) { $fileStream.Dispose() }
        if ($client) { $client.Dispose() }
    }
}

Write-Host ""

# ---- 3. 回读校验 ----
$verify = Invoke-RestMethod -Uri "$api/releases/tags/$Tag" -Method Get -TimeoutSec 30

Write-Host "=== 发行版标题 ==="
Write-Host "  $($verify.name)"
Write-Host ""
Write-Host "=== 附件清单 ==="
$matched = $null
foreach ($asset in $verify.assets) {
    Write-Host ("  {0}" -f $asset.name)
    Write-Host ("    {0}" -f $asset.browser_download_url)
    if ($asset.name -eq $zip.Name) { $matched = $asset }
}

Write-Host ""
if ($null -eq $matched) {
    Write-Warning "附件列表里没看到 $($zip.Name),请到网页确认"
    exit 1
}

# ---- 4. 校验下载链接可用 ----
Write-Host "=== 校验下载链接 ==="
try {
    $req = [System.Net.HttpWebRequest]::Create($matched.browser_download_url)
    $req.Timeout = 20000
    $req.AddRange(0, 1023)
    $resp = $req.GetResponse()
    $code = [int]$resp.StatusCode
    $resp.Close()
    Write-Host "  HTTP $code"
    if ($code -eq 206) {
        Write-Host "  ✅ 支持 Range(可断点续传)"
    }
    else {
        Write-Host "  ⚠️ 不支持 Range(200),续传会退化为重下;Gitee 速度较快,一般可接受"
        Write-Host "     (这是 Gitee 平台限制,而非脚本问题)"
    }
}
catch {
    Write-Warning "  下载链接验证失败:$($_.Exception.Message)"
}

Write-Host ""
Write-Host "✅ 完成:$Tag 已在 Gitee 发布"
Write-Host ""
Write-Host "更新器自检(应命中 Gitee):"
Write-Host "  .\publish-beta\Daybreak.Updater.exe --check"
