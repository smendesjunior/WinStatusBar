# Liga o Windows Sandbox (recurso oficial do Windows), usado para testar o instalador num ambiente descartável.
# Roda fora do pacote do Claude (aberto pelo ligar-sandbox.cmd via explorer.exe) e pede administrador pelo UAC.
param([switch]$Elevated)
$ErrorActionPreference = 'Stop'
$log = Join-Path $PSScriptRoot 'ligar-sandbox.log'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    if ($Elevated) { "$(Get-Date -Format s) ERRO: sem administrador mesmo após o UAC." | Set-Content $log -Encoding UTF8; exit 1 }
    Start-Process powershell.exe -Verb RunAs -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Elevated'
    exit 0
}
try {
    $feature = Get-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM
    if ($feature.State -eq 'Enabled') { $result = 'já estava ligado' }
    else {
        $r = Enable-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM -All -NoRestart
        $result = if ($r.RestartNeeded) { 'ligado; falta reiniciar o PC' } else { 'ligado' }
    }
    "$(Get-Date -Format s) Windows Sandbox: $result" | Set-Content $log -Encoding UTF8
    Write-Host "Windows Sandbox: $result" -ForegroundColor Green
} catch {
    "$(Get-Date -Format s) ERRO: $($_.Exception.Message)" | Set-Content $log -Encoding UTF8
    Write-Host "Falha: $($_.Exception.Message)" -ForegroundColor Red
}
Start-Sleep -Seconds 5
