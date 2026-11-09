# Marca ESTE PC (desenvolvedor) para se atualizar só pela pasta local Instalador, e não pelo GitHub.
# Roda fora do pacote do Claude (aberto pelo .cmd via explorer.exe): grava em %LOCALAPPDATA%\WinStatusBar.
# As versões 1.3.x continuam lendo LocalDirectory; a 1.4.0+ exige "Mode": "Local" (sem ele, usa o GitHub).
$ErrorActionPreference = 'Stop'
$log = Join-Path $PSScriptRoot 'fonte-local-deste-pc.log'
$folder = 'C:\Users\smendesjunior\OneDrive\Aplicativos IA\WinStatusBar\Instalador'
try {
    if (-not (Test-Path -LiteralPath $folder)) { throw "Pasta não encontrada: $folder" }
    $dir = Join-Path $env:LOCALAPPDATA 'WinStatusBar'
    New-Item -ItemType Directory -Force $dir | Out-Null
    $file = Join-Path $dir 'update-source.json'
    $json = [ordered]@{ Mode = 'Local'; LocalDirectory = $folder } | ConvertTo-Json -Compress
    [IO.File]::WriteAllText($file, $json, (New-Object Text.UTF8Encoding $false))
    $back = [IO.File]::ReadAllText($file)
    if ($back -notmatch '"Mode":\s*"Local"') { throw "Conferência falhou: $back" }
    "$(Get-Date -Format s) OK $file = $back" | Set-Content $log -Encoding UTF8
} catch {
    "$(Get-Date -Format s) ERRO: $($_.Exception.Message)" | Set-Content $log -Encoding UTF8
}
