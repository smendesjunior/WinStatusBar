# Atualiza a barra instalada (%LOCALAPPDATA%\Programs\WinStatusBar) com artifacts\update-app, fora do pacote do Claude.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$src = Join-Path $root 'artifacts\update-app'
$dst = Join-Path $env:LOCALAPPDATA 'Programs\WinStatusBar'
$log = Join-Path $root 'artifacts\update-installed.log'
Start-Transcript -Path $log -Force | Out-Null
try {
    & (Join-Path $PSScriptRoot 'stop-bar.ps1')
    Get-Process WinStatusBar -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    # O coletor dos hooks do Claude roda a partir desta pasta por instantes e pode prender uma DLL: tenta de novo.
    for ($try = 1; ; $try++) {
        try { Copy-Item (Join-Path $src '*') $dst -Recurse -Force -ErrorAction Stop; break }
        catch { if ($try -ge 15) { throw }; "arquivo em uso (tentativa $try): $($_.Exception.Message)"; Start-Sleep -Milliseconds 800 }
    }
    "copiado para $dst"
} catch { "ERRO: $_" }
# Reabre a barra mesmo se a cópia falhar: nunca deixa a pessoa sem a barra.
try {
    Start-Process (Join-Path $dst 'WinStatusBar.exe')
    Start-Sleep -Seconds 4
    $p = Get-Process WinStatusBar -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($p) { "em execução: PID $($p.Id), iniciado $($p.StartTime)" } else { "NÃO está em execução" }
    "WinStatusBar.dll: " + (Get-Item (Join-Path $dst 'WinStatusBar.dll')).LastWriteTime
} catch { "ERRO: $_" }
Stop-Transcript | Out-Null
