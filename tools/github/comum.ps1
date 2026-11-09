# Funções comuns dos scripts de publicação no GitHub (carregue com: . (Join-Path $PSScriptRoot 'comum.ps1')).

function Find-Gh {
    $cmd = Get-Command gh -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($p in "$env:ProgramFiles\GitHub CLI\gh.exe", "$env:LOCALAPPDATA\Programs\GitHub CLI\gh.exe") { if (Test-Path -LiteralPath $p) { return $p } }
    return $null
}

# Programas externos (git, gh) escrevem progresso no stderr: lê como texto e decide pelo código de saída.
function Invoke-Native([string]$Exe, [string[]]$Arguments, [string]$What) {
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { $out = @(& $Exe @Arguments 2>&1 | ForEach-Object { "$_" }) } finally { $ErrorActionPreference = $old }
    if ($LASTEXITCODE -ne 0) { throw "$What falhou (código $LASTEXITCODE): $($out -join ' ')" }
    return $out
}

# Notas da Release (inglês): seção da versão no CHANGELOG.en.md + como instalar.
function Get-ReleaseNotes([string]$Root, [string]$Version) {
    $english = [IO.File]::ReadAllText((Join-Path $Root 'CHANGELOG.en.md'))
    $section = [regex]::Match($english, "(?ms)^## \[$([regex]::Escape($Version))\][^\r\n]*\r?\n(.*?)(?=^## \[|\z)")
    if (-not $section.Success -or -not $section.Groups[1].Value.Trim()) { throw "Falta a seção [$Version] no CHANGELOG.en.md." }
    $install = @'
## Install
- **winget** (no SmartScreen prompt): `winget install winstatusbar`
- **Installer**: download `WinStatusBar-Setup.exe` below and run it. Windows may show a SmartScreen prompt the first time (More info › Run anyway), because the installer is not code-signed yet.

Existing installations update automatically and silently.
'@
    return $section.Groups[1].Value.Trim() + "`n`n" + $install
}
