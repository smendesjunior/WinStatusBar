# Prepara este PC para publicar o WinStatusBar no GitHub (roda fora do pacote do Claude, pelo configurar-github.cmd).
# - instala o GitHub CLI e o wingetcreate se faltarem (winget, oficiais);
# - login no GitHub pelo navegador, se ainda não houver (feito por você; nenhuma senha passa por aqui);
# - git push passa a usar o login do GitHub CLI;
# - textos do repositório (página inicial e tópicos), em inglês.
# Registro (sem tokens): configurar-github.log, nesta pasta.
$ErrorActionPreference = 'Stop'
$log = Join-Path $PSScriptRoot 'configurar-github.log'
$lines = New-Object Collections.Generic.List[string]
function Say([string]$text) { Write-Host $text; $lines.Add("$(Get-Date -Format s) $text") }
function Find-Gh {
    $cmd = Get-Command gh -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($p in "$env:ProgramFiles\GitHub CLI\gh.exe", "$env:LOCALAPPDATA\Programs\GitHub CLI\gh.exe") { if (Test-Path -LiteralPath $p) { return $p } }
    return $null
}
function Native([scriptblock]$Command) {
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { & $Command } finally { $ErrorActionPreference = $old }
    return $LASTEXITCODE
}
try {
    $gh = Find-Gh
    if (-not $gh) {
        Say 'Instalando o GitHub CLI (winget)...'
        [void](Native { winget install --id GitHub.cli -e --source winget --accept-source-agreements --accept-package-agreements })
        $gh = Find-Gh
        if (-not $gh) { throw 'GitHub CLI não foi instalado.' }
    }
    Say "GitHub CLI: $gh"
    $wingetCreate = Get-Command wingetcreate -ErrorAction SilentlyContinue
    if (-not $wingetCreate) {
        Say 'Instalando o wingetcreate (winget)...'
        [void](Native { winget install --id Microsoft.WingetCreate -e --source winget --accept-source-agreements --accept-package-agreements })
    }
    Say ("wingetcreate: " + $(if (Get-Command wingetcreate -ErrorAction SilentlyContinue) { 'instalado' } else { 'ainda não encontrado (abra um terminal novo e confira)' }))

    if ((Native { & $gh auth status *> $null }) -ne 0) {
        Say 'Entre no GitHub pelo navegador (copie o código mostrado abaixo)...'
        if ((Native { & $gh auth login --hostname github.com --git-protocol https --web }) -ne 0) { throw 'Login no GitHub não concluído.' }
    }
    if ((Native { & $gh auth setup-git --hostname github.com }) -ne 0) { throw 'Não foi possível ligar o git ao login do GitHub CLI.' }
    $login = (& $gh api user --jq .login).Trim()
    if ($login -ne 'smendesjunior') { throw "Login atual é '$login'; esperado: smendesjunior." }
    Say "login: $login (git push usa o GitHub CLI)"

    $topics = 'windows,windows-11,status-bar,appbar,winui3,dotnet,system-monitor,ai-assistant,claude,codex,gemini,copilot'
    if ((Native { & $gh repo edit smendesjunior/WinStatusBar --homepage 'https://github.com/smendesjunior/WinStatusBar/releases/latest' --add-topic $topics }) -ne 0) { throw 'Não foi possível atualizar os textos do repositório.' }
    Say 'Repositório: página inicial e tópicos atualizados.'
    Say 'CONCLUÍDO'
} catch {
    Say "ERRO: $($_.Exception.Message)"
}
$lines | Set-Content $log -Encoding UTF8
Read-Host 'Pressione Enter para fechar'
