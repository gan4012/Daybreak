<#
.SYNOPSIS
    扫描仓库里有没有敏感信息(服务器 IP、密码、密钥、令牌)。

.DESCRIPTION
    用户的硬性要求:仓库是公开的，不许把密码、IP、密钥写进任何文件。

    这个脚本有两种用法:
      -Staged    只扫已暂存的文件(pre-commit 钩子用)
      -All       扫全部被 git 跟踪的文件(定期自查用)

    发现敏感内容返回退出码 1，否则 0。

.EXAMPLE
    powershell -File scripts/check-secrets.ps1 -Staged
    powershell -File scripts/check-secrets.ps1 -All
#>
[CmdletBinding()]
param(
    [switch]$Staged,
    [switch]$All
)

$ErrorActionPreference = "Stop"

if (-not $Staged -and -not $All) {
    Write-Host "用法:" -ForegroundColor Yellow
    Write-Host "  powershell -File scripts/check-secrets.ps1 -Staged   # 扫暂存区(pre-commit 用)"
    Write-Host "  powershell -File scripts/check-secrets.ps1 -All      # 扫全部跟踪文件"
    exit 2
}

# ---- 允许出现的「不是敏感信息」的 IP ----
# 这些都是文档里必然会用到的保留地址 / 示例地址
$ipAllowList = @(
    "0\.0\.0\.0",           # 监听全部网卡
    "127\.0\.0\.1",         # 本机回环
    "255\.255\.255\.255",   # 广播
    "10\.144\.144\.",       # EasyTier 虚拟网段(房间号派生，非公网)
    "192\.168\.",           # 内网
    "10\.0\.", "10\.1\.", "172\.16\.",  # 常见内网段
    "1\.1\.1\.1", "8\.8\.8\.8",          # 公共 DNS(文档里作示例)
    "223\.5\.5\.5", "119\.29\.29\.29", "114\.114\.114\.114",  # 国内公共 DNS(代码里会用到)
    "224\.0\.2\.60"         # Minecraft 组播地址
)

$findings = New-Object System.Collections.ArrayList

function Add-Finding {
    param($File, $Line, $Kind, $Text)
    [void]$findings.Add([pscustomobject]@{
        File = $File; Line = $Line; Kind = $Kind
        Text = ($Text.Trim() -replace '\s+', ' ')
    })
}

# ---- 要检查的文本类文件 ----
$textExt = @(".cs", ".xaml", ".csproj", ".props", ".slnx", ".ps1", ".py", ".js", ".ts",
             ".json", ".yml", ".yaml", ".md", ".txt", ".html", ".css", ".sh", ".xml",
             ".gitignore", ".editorconfig", ".config")

function Test-TextFile {
    param($Path)
    $name = [System.IO.Path]::GetFileName($Path)
    if ($name -match '^\.(gitignore|editorconfig|assetsignore)$') { return $true }
    $ext = [System.IO.Path]::GetExtension($Path).ToLower()
    return ($textExt -contains $ext)
}

# ---- 取要检查的文件列表 ----
$files = @()
if ($Staged) {
    $files = git -c core.quotepath=false diff --cached --name-only --diff-filter=ACM 2>&1 | Where-Object { $_ -and $_.Trim() }
}
else {
    $files = git -c core.quotepath=false ls-files 2>&1 | Where-Object { $_ -and $_.Trim() }
}

# 这些路径本身就不该被扫(供应商代码、测试数据等)
$skipPath = @("third_party/", "src/Daybreak.Core.Tests/TestData", "dist/")

$checked = 0
foreach ($f in $files) {
    # git 默认会把非 ASCII 文件名转义成八进制，所以上面加了 core.quotepath=false。
    # 这里再兜一层:去掉可能残留的包裹引号。
    $f = $f.Trim('"')
    $rel = $f -replace '\\', '/'
    $skip = $false
    foreach ($s in $skipPath) { if ($rel.StartsWith($s)) { $skip = $true; break } }
    if ($skip) { continue }

    if (-not (Test-Path -LiteralPath $f)) { continue }   # 已删除的文件
    if (-not (Test-TextFile $f)) { continue }

    $checked++
    $raw = [System.IO.File]::ReadAllText((Resolve-Path -LiteralPath $f).Path, [System.Text.Encoding]::UTF8)
    $lines = $raw -split "`r?`n"

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $ln = $lines[$i]
        $num = $i + 1

        # ---- 1. 公网 IP ----
        foreach ($m in [regex]::Matches($ln, '\b(?:\d{1,3}\.){3}\d{1,3}\b')) {
            $ip = $m.Value
            $allowed = $false
            foreach ($a in $ipAllowList) { if ($ip -match "^$a") { $allowed = $true; break } }
            # 本地回环/内网第一段也放过
            if ($ip -match '^(0|127|10|192\.168|255)\.') { $allowed = $true }
            if (-not $allowed) {
                # 版本号形如 1.20.1 也会被这个正则命中，排除掉
                $parts = $ip -split '\.'
                $looksLikeVersion = $true
                foreach ($p in $parts) { if ([int]$p -gt 255) { $looksLikeVersion = $false } }
                if ($parts[0] -eq "1" -or $parts[0] -eq "2" -or $parts[0] -eq "3" -or
                    $parts[0] -eq "4" -or $parts[0] -eq "5" -or $parts[0] -eq "6") {
                    # 1.x/2.x 这种开头多半是版本号，除非四段都在合理 IP 范围且不像版本
                    if ($parts[0] -le 9 -and $parts[1] -le 30) { continue }
                }
                Add-Finding $f $num "公网IP" $ip
            }
        }

        # ---- 2. 密码 / 口令 ----
        if ($ln -match '(?i)(password|passwd|pwd)\s*[:=]\s*\S' -and
            $ln -notmatch '(?i)(password\s*[:=]\s*(""|''''|\{|<|\.\.\.|你的|xxx|\$\{))' -and
            $ln -notmatch '(?i)(通?密码|Password)\s*[:=]\s*$' -and
            $ln -notmatch '密码.*不进 git' -and
            $ln -notmatch '^\s*//' -and $ln -notmatch '^\s*#') {
            Add-Finding $f $num "疑似密码" $ln
        }
        if ($ln -match '密码\s*[:=]\s*[A-Za-z0-9!@#\$%^&*]{6,}' -and $ln -notmatch '不进 git|占位|你的密码|示例') {
            Add-Finding $f $num "疑似密码" $ln
        }

        # ---- 3. 私钥 ----
        if ($ln -match 'BEGIN (RSA |EC |OPENSSH |DSA |PGP )?PRIVATE KEY') {
            Add-Finding $f $num "私钥" $ln
        }

        # ---- 4. 令牌 / 密钥 ----
        # 只认「像密钥的字段名 + 一长串带引号的随机值」这个组合。
        # 反例(不该报):
        #   cancellationToken: CancellationToken.None     ← 代码标识符
        #   AccessToken = yggSession.AccessToken           ← 属性赋值
        #   var accessToken = root.GetProperty("accessToken")  ← 读 JSON 字段
        # 正例(该报):
        #   api_key = "一长串二十位以上的随机字符"
        #
        # 注意:脚本自己的注释里也会出现类似写法，
        # 所以规则里排除了「带占位/示例/下面这种说明性文字」的行。
        if ($ln -match '(?i)\b(api[_-]?key|apikey|client[_-]?secret|access[_-]?key[_-]?secret|auth[_-]?token|api[_-]?token)\b\s*[:=]\s*["'']([A-Za-z0-9_\-\.]{20,})["'']' -and
            $ln -notmatch '(?i)(apiKeyEnv|Environment|GetEnvironmentVariable|\$\{|<|占位|示例|你的|随机字符|字段名|一长串)') {
            Add-Finding $f $num "疑似令牌" $ln
        }

        # ---- 5. 特定云厂商密钥特征 ----
        if ($ln -match 'AKID[A-Za-z0-9]{13,}' -or $ln -match 'ghp_[A-Za-z0-9]{20,}' -or
            $ln -match 'sk-[A-Za-z0-9]{20,}') {
            Add-Finding $f $num "云厂商密钥" $ln
        }
    }
}

# ---- 输出 ----
Write-Host ""
Write-Host "=== 敏感信息扫描 ($(if ($Staged) { '暂存区' } else { '全部跟踪文件' })) ===" -ForegroundColor Cyan
Write-Host "  检查了 $checked 个文本文件"

if ($findings.Count -eq 0) {
    Write-Host "  ✅ 没发现问题" -ForegroundColor Green
    exit 0
}

Write-Host ""
Write-Host "  ❌ 发现 $($findings.Count) 处可疑内容:" -ForegroundColor Red
Write-Host ""
foreach ($fd in $findings) {
    Write-Host ("  [{0}] {1}:{2}" -f $fd.Kind, $fd.File, $fd.Line) -ForegroundColor Red
    Write-Host ("        {0}" -f $fd.Text) -ForegroundColor DarkGray
}
Write-Host ""
Write-Host "  处理办法:" -ForegroundColor Yellow
Write-Host "    1. 把地址/密钥换成占位符，例如 <中继地址见 relay.txt，不进 git>"
Write-Host "    2. 真实值只放到 gitignored 的文件或环境变量里"
Write-Host "    3. 确认无误后重新提交"
Write-Host ""
Write-Host "  误报?把该文件加进本脚本的 `$skipPath 或调整允许列表。" -ForegroundColor DarkGray
exit 1