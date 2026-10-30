<#
.SYNOPSIS
    修复仓库里所有 .ps1 的编码(UTF-8 BOM + CRLF)，并校验语法。

.DESCRIPTION
    为什么需要这个:
    本仓库的 PowerShell 脚本含中文，PS 5.1 在**没有 BOM** 时会按 ANSI 解码，
    导致中文字符串被打断，报 `Unexpected token ')'` 且错误信息里的中文全是乱码。
    编辑工具(edit/write)会静默剥掉 BOM —— 这个坑已经踩过不止一次，
    2026-10-30 的 v0.1.4 发版就是因为它 CI 挂了。

    所以:每次用编辑工具改完含中文的 .ps1，跑一遍这个脚本。

    行为:
      · 默认只检查并报告，不修改
      · -Fix 才真的写回(BOM + CRLF)
      · 任何脚本有语法错误 → 退出码 1

.EXAMPLE
    powershell -File scripts/fix-script-encoding.ps1
    powershell -File scripts/fix-script-encoding.ps1 -Fix
#>
[CmdletBinding()]
param(
    [switch]$Fix
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

# 本次扫描的范围:scripts/ 和 tools/ 下的所有 .ps1
$targets = @()
$targets += Get-ChildItem (Join-Path $root "scripts") -Filter "*.ps1" -Recurse -File -ErrorAction SilentlyContinue
$targets += Get-ChildItem (Join-Path $root "tools") -Filter "*.ps1" -Recurse -File -ErrorAction SilentlyContinue
$targets = $targets | Sort-Object FullName -Unique

if ($targets.Count -eq 0) {
    Write-Host "没找到任何 .ps1 文件" -ForegroundColor Yellow
    exit 0
}

$problems = 0
$syntaxErrors = 0

Write-Host ""
Write-Host "=== 检查 $($targets.Count) 个 PowerShell 脚本 ===" -ForegroundColor Cyan
Write-Host ""

foreach ($f in $targets) {
    $rel = $f.FullName.Replace($root + "\", "")
    $bytes = [System.IO.File]::ReadAllBytes($f.FullName)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $text = [System.IO.File]::ReadAllText($f.FullName, (New-Object System.Text.UTF8Encoding($false)))
    $bareLf = [regex]::Matches($text, "(?<!`r)`n").Count

    # 只有含中文的脚本才强制要求 BOM(纯 ASCII 的脚本有没有 BOM 都能跑)
    $hasChinese = $text -match '[\u4e00-\u9fff]'
    $needBom = $hasChinese -and -not $hasBom
    $needCrlf = $bareLf -gt 0

    if ($needBom -or $needCrlf) {
        $problems++
        $why = @()
        if ($needBom) { $why += "缺 BOM(含中文，PS 5.1 会解析失败)" }
        if ($needCrlf) { $why += "$bareLf 处裸 LF" }
        Write-Host "  ⚠️  $rel" -ForegroundColor Yellow
        $why | ForEach-Object { Write-Host "        $($_)" -ForegroundColor Yellow }

        if ($Fix) {
            $fixed = ($text -replace "`r`n", "`n") -replace "`n", "`r`n"
            [System.IO.File]::WriteAllText($f.FullName, $fixed, (New-Object System.Text.UTF8Encoding($true)))
            Write-Host "        → 已修复" -ForegroundColor Green
        }
    }
    else {
        Write-Host "  ✅ $rel" -ForegroundColor DarkGreen
    }
}

# ---- 语法校验(修复后再验，修完还有错就是真错) ----
Write-Host ""
Write-Host "=== 语法校验 ===" -ForegroundColor Cyan
foreach ($f in $targets) {
    $rel = $f.FullName.Replace($root + "\", "")
    $errs = $null
    [System.Management.Automation.Language.Parser]::ParseFile($f.FullName, [ref]$null, [ref]$errs) | Out-Null
    if ($errs -and $errs.Count -gt 0) {
        $syntaxErrors++
        Write-Host "  ❌ $rel  ($($errs.Count) 处)" -ForegroundColor Red
        $errs | Select-Object -First 3 | ForEach-Object {
            Write-Host "        行 $($_.Extent.StartLineNumber): $($_.Message)" -ForegroundColor Red
        }
    }
}
if ($syntaxErrors -eq 0) {
    Write-Host "  ✅ 全部通过" -ForegroundColor Green
}

# ---- 总结 ----
Write-Host ""
if ($problems -gt 0 -and -not $Fix) {
    Write-Host "发现 $problems 个编码问题。加 -Fix 参数修复:" -ForegroundColor Yellow
    Write-Host "    powershell -File scripts/fix-script-encoding.ps1 -Fix" -ForegroundColor Yellow
    exit 1
}
elseif ($problems -gt 0 -and $Fix) {
    Write-Host "已修复 $problems 个编码问题。" -ForegroundColor Green
}

if ($syntaxErrors -gt 0) {
    Write-Host "有 $syntaxErrors 个脚本存在语法错误，必须手工修。" -ForegroundColor Red
    exit 1
}

Write-Host "全部正常。" -ForegroundColor Green
exit 0