# Liga a ponte local do WinStatusBar em todo Hub do Lenovo Qira (QuantumAI.Hub.exe), inclusive no que o próprio Qira abre.
# Política oficial do WebView2 (AdditionalBrowserArguments por aplicativo): depuração só em 127.0.0.1, porta 9339,
# sem --remote-allow-origins (páginas da web não conseguem se conectar). Vale só para QuantumAI.Hub.exe.
param([switch]$Remover)
$key = 'HKLM:\SOFTWARE\Policies\Microsoft\Edge\WebView2\AdditionalBrowserArguments'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
  $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
  if ($Remover) { $a += '-Remover' }
  Start-Process powershell.exe -Verb RunAs -ArgumentList $a -Wait
  exit
}
if ($Remover) {
  Remove-ItemProperty -Path $key -Name 'QuantumAI.Hub.exe' -ErrorAction SilentlyContinue
  Write-Host 'Ponte do Qira removida. Reinicie o Qira (bandeja › Sair) para valer.'
} else {
  New-Item -Path $key -Force | Out-Null
  Set-ItemProperty -Path $key -Name 'QuantumAI.Hub.exe' -Value '--remote-debugging-port=9339'
  Write-Host 'Ponte do Qira ativada (porta 9339, só neste PC). Saia do Qira pela bandeja e abra de novo para valer.'
}
$valor = (Get-ItemProperty -Path $key -ErrorAction SilentlyContinue).'QuantumAI.Hub.exe'
Write-Host "Valor atual: $valor"
"$(Get-Date -Format 's') $(if ($Remover) { 'remover' } else { 'ativar' }): valor='$valor'" | Add-Content (Join-Path $PSScriptRoot 'resultado.txt') -Encoding UTF8
Start-Sleep -Seconds 6
