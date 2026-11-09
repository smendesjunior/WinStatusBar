Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class DB { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); }
'@
[void][DB]::SetProcessDPIAware()
$A = [System.Windows.Automation.AutomationElement]; $T = [System.Windows.Automation.TreeScope]
$pids = @(Get-Process WinStatusBar | ForEach-Object Id)
foreach ($w in @($A::RootElement.FindAll($T::Children, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $pids -contains $_.Current.ProcessId })) {
  $r = $w.Current.BoundingRectangle
  "JANELA '$($w.Current.Name)' class=$($w.Current.ClassName) rect=$([int]$r.X),$([int]$r.Y),$([int]$r.Width)x$([int]$r.Height) offscreen=$($w.Current.IsOffscreen)"
  if ($r.Y -lt 5 -and $r.Height -lt 200 -and $r.Width -gt 1000) {
    foreach ($e in @($w.FindAll($T::Descendants, [System.Windows.Automation.Condition]::TrueCondition))) {
      $er = $e.Current.BoundingRectangle
      if ($e.Current.ControlType.ProgrammaticName -match 'Button|Group|Custom|Pane' -and $er.Width -gt 0) { "  $($e.Current.ControlType.ProgrammaticName -replace 'ControlType.','') name='$($e.Current.Name)' auto='$($e.Current.AutomationId)' rect=$([int]$er.X),$([int]$er.Y),$([int]$er.Width)x$([int]$er.Height)" }
    }
  }
}
