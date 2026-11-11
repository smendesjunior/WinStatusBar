# Procura chaves, tokens, certificados e nomes internos no que vai para o repositório PÚBLICO. Para tudo se achar algo.
# Uso: verificar-segredos.ps1                    → arquivos preparados para o commit (git diff --cached)
#      verificar-segredos.ps1 -Paths <lista>     → conteúdo atual dos arquivos dados (ex.: (git ls-files))
#      verificar-segredos.ps1 -Range <intervalo> → linhas ADICIONADAS nos commits do intervalo (ex.: origin/main..HEAD;
#                                                  um único commit, como HEAD, cobre todo o histórico até ele)
param([string[]]$Paths, [string]$Range)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
# Os dois arquivos que tratam de segredos de propósito (máscara e testes com valores falsos).
$allowed = @('src/WinStatusBar.Core/Clip/SecretMask.cs', 'tests/WinStatusBar.Tests/SecretMaskTests.cs')
$blockedExtensions = @('.pfx', '.p12', '.pem', '.key')
# Tokens e chaves + nomes da rede interna e da empresa, que ficam em nomes-internos.txt (fora do git: a lista não
# pode ir para o repositório público). Uma expressão regular por linha.
# "sk-" só conta no começo de uma palavra: "task-0123…" (identificador de tarefa) não é chave.
$pattern = '(?<![A-Za-z0-9])sk-(proj-|svcacct-|admin-|ant-)?[A-Za-z0-9_\-]{20,}|gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,}|AIza[0-9A-Za-z_\-]{35}|AKIA[0-9A-Z]{16}|xox[abpr]-[A-Za-z0-9-]{10,}|-----BEGIN [A-Z ]*PRIVATE KEY-----'
$internal = Join-Path $PSScriptRoot 'nomes-internos.txt'
if (Test-Path -LiteralPath $internal) {
    $names = @(Get-Content -LiteralPath $internal -Encoding UTF8 | Where-Object { $_.Trim() })
    if ($names) { $pattern += '|' + ($names -join '|') }
} else { Write-Warning 'nomes-internos.txt ausente: nomes internos não serão conferidos.' }
Push-Location $root
try {
    $hits = New-Object Collections.Generic.List[string]
    if ($Range) {
        # git log -p: cabeçalhos "+++ b/<arquivo>" e linhas "+..." adicionadas, com caminhos sem aspas (acentos).
        $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        $lines = & git -c core.quotePath=false log -p --no-color --format='commit %h' $Range 2>&1
        $code = $LASTEXITCODE; $ErrorActionPreference = $old
        if ($code -ne 0) { throw "git log falhou para '$Range': $($lines -join ' ')" }
        $commit = ''; $file = ''
        foreach ($line in $lines) {
            $text = "$line"
            if ($text -match '^commit ([0-9a-f]+)$') { $commit = $Matches[1]; continue }
            if ($text.StartsWith('+++ ')) { $file = $text.Substring(4) -replace '^b/', ''; if ($blockedExtensions -contains [IO.Path]::GetExtension($file).ToLowerInvariant()) { $hits.Add("$commit $file (arquivo de chave ou certificado)") }; continue }
            if ($allowed -contains $file) { continue }
            if ($text.StartsWith('+') -and $text -match $pattern) { $hits.Add("$commit ${file}: $($Matches[0])") }
        }
    } else {
        if (-not $Paths) { $Paths = @(git -c core.quotePath=false diff --cached --name-only --diff-filter=ACMR) }
        foreach ($path in $Paths) {
            $name = $path -replace '\\', '/'
            if ($allowed -contains $name) { continue }
            if ($blockedExtensions -contains [IO.Path]::GetExtension($name).ToLowerInvariant()) { $hits.Add("$name (arquivo de chave ou certificado)"); continue }
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Arquivo para conferir não encontrado: $path" }
            Select-String -LiteralPath $path -Pattern $pattern -ErrorAction SilentlyContinue | ForEach-Object { $hits.Add("$($name):$($_.LineNumber): $($_.Matches[0].Value)") }
        }
    }
    if ($hits.Count) { throw ("Possível segredo ou nome interno encontrado; nada foi enviado ao GitHub:`n" + (($hits | Select-Object -Unique) -join "`n")) }
    Write-Host 'Nenhum segredo encontrado.'
} finally { Pop-Location }
