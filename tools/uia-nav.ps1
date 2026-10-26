param([string]$name, [string]$kind = "nav")

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class FG3 { [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); }
'@
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$AE = [System.Windows.Automation.AutomationElement]
$proc = Get-Process Daybreak.Launcher | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { Write-Output "no-window"; exit 1 }

[FG3]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 700

$root = $AE::FromHandle($proc.MainWindowHandle)
$all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)

# 找到名字匹配的文本元素
$target = $null
foreach ($el in $all) {
    if ($el.Current.Name -eq $name) { $target = $el; break }
}
if (-not $target) { Write-Output "text-not-found: $name"; exit 1 }

# 往上找可选择的祖先(ListBoxItem / RadioButton / TabItem)
$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
$node = $target
for ($i = 0; $i -lt 8 -and $node; $i++) {
    try {
        $si = $node.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $si.Select()
        Write-Output "selected: $name (level $i, type $($node.Current.ControlType.ProgrammaticName))"
        exit 0
    } catch { }

    try {
        $inv = $node.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $inv.Invoke()
        Write-Output "invoked: $name (level $i, type $($node.Current.ControlType.ProgrammaticName))"
        exit 0
    } catch { }

    try {
        $tg = $node.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        $tg.Toggle()
        Write-Output "toggled: $name (level $i, type $($node.Current.ControlType.ProgrammaticName))"
        exit 0
    } catch { }

    $node = $walker.GetParent($node)
}

Write-Output "no-pattern-found: $name"
