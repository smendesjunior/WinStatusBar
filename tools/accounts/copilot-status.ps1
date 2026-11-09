$ErrorActionPreference = 'Stop'
$result = @{ CheckedAt=[DateTimeOffset]::UtcNow.ToString('o'); Account=$null; Email=$null; Error=$null }
try {
  Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
  $path = Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Copilot\Application\copilotapp.exe'
  if (-not (Test-Path -LiteralPath $path)) { throw 'Microsoft Copilot nao encontrado neste PC.' }
  Start-Process -FilePath $path
  $account = $null
  foreach ($attempt in 1..30) {
    $pids = @(Get-Process copilotapp -ErrorAction SilentlyContinue | Where-Object Path -eq $path | ForEach-Object Id)
    $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.Condition]::TrueCondition)
    foreach ($window in $windows) {
      if ($window.Current.ProcessId -notin $pids) { continue }
      $nodes = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
      $accounts = @($nodes | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and $_.Current.Name -match ', (Conta pessoal|Personal account)$' })
      if ($accounts.Count -eq 1) { $account = $accounts[0]; break }
    }
    if ($account) { break }
    Start-Sleep -Milliseconds 600
  }
  if (-not $account) { throw 'Conta nao exposta pela acessibilidade. Confira login e e-mail no aplicativo oficial.' }
  $result.Account = $account.Current.Name
  $account.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Milliseconds 500
  $nodes = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
  $emails = @($nodes | ForEach-Object { if ($_.Current.Name -match '^[^\s@]+@[^\s@]+\.[^\s@]+$') { $_.Current.Name } } | Select-Object -Unique)
  if ($emails.Count -eq 1) { $result.Email = $emails[0] }
  # Account menu stays open for the person to inspect or sign out manually. Never invoke sign-out or login here.
} catch { $result.Error = $_.Exception.Message }
$file = Join-Path $PSScriptRoot 'copilot-account-status.json'
[IO.File]::WriteAllText($file,($result | ConvertTo-Json -Compress),(New-Object Text.UTF8Encoding($false)))
