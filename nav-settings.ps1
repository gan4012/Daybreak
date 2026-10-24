Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class FG { [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); }
'@
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$proc = Get-Process Daybreak.Launcher | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
[FG]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 500
$root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
$all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
foreach ($el in $all) {
  if ($el.Current.Name -eq '设置') {
    try { $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Write-Output 'selected'; exit 0 } catch {}
    try { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Write-Output 'invoked'; exit 0 } catch {}
  }
}
Write-Output 'not-found'
exit 1
