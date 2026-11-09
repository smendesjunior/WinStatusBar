# Procura chaves, tokens e certificados no que vai para o repositório PÚBLICO. Para tudo se achar algo.
# Uso: verificar-segredos.ps1                → confere os arquivos preparados para o commit (git diff --cached)
#      verificar-segredos.ps1 -Paths <lista> → confere a lista dada (ex.: (git ls-files))
param([string[]]$Paths)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Push-Location $root
try {
    if (-not $Paths) { $Paths = @(git diff --cached --name-only --diff-filter=ACMR) }
    # Os dois arquivos que tratam de segredos de propósito (máscara e testes com valores falsos).
    $allowed = @('src/WinStatusBar.Core/Clip/SecretMask.cs', 'tests/WinStatusBar.Tests/SecretMaskTests.cs')
    $blockedExtensions = @('.pfx', '.p12', '.pem', '.key')
    $pattern = 'sk-(proj-|svcacct-|admin-|ant-)?[A-Za-z0-9_\-]{20,}|gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,}|AIza[0-9A-Za-z_\-]{35}|AKIA[0-9A-Z]{16}|xox[abpr]-[A-Za-z0-9-]{10,}|-----BEGIN [A-Z ]*PRIVATE KEY-----'
    $hits = foreach ($path in $Paths) {
        $name = $path -replace '\\', '/'
        if ($allowed -contains $name) { continue }
        if ($blockedExtensions -contains [IO.Path]::GetExtension($name).ToLowerInvariant()) { "$name (arquivo de chave ou certificado)"; continue }
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        Select-String -LiteralPath $path -Pattern $pattern -ErrorAction SilentlyContinue | ForEach-Object { "$($name):$($_.LineNumber)" }
    }
    if ($hits) { throw ("Possível segredo encontrado; nada foi enviado ao GitHub:`n" + ($hits -join "`n")) }
    Write-Host 'Nenhum segredo encontrado.'
} finally { Pop-Location }
