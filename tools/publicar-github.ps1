# Publica uma versão no GitHub: commit + tag + push do código e Release com o instalador e o manifesto.
# Chamado pelo build-release.ps1 depois de o instalador estar pronto e conferido em Instalador.
# A Release nasce como rascunho e só é publicada com os dois arquivos enviados: os PCs nunca veem uma Release pela metade.
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Build,
    [Parameter(Mandatory)][string]$Setup,
    [Parameter(Mandatory)][string]$Manifest
)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$root = Split-Path $PSScriptRoot -Parent
$repo = 'smendesjunior/WinStatusBar'
$tag = "v$Version"
$latestManifest = "https://github.com/$repo/releases/latest/download/WinStatusBar-update.json"

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

$gh = Find-Gh
if (-not $gh) { throw 'GitHub CLI não encontrado. Rode tools\github\configurar-github.cmd.' }
$old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
& $gh auth status *> $null; $authOk = $LASTEXITCODE -eq 0
$ErrorActionPreference = $old
if (-not $authOk) { throw 'GitHub CLI sem login. Rode tools\github\configurar-github.cmd.' }
foreach ($file in $Setup, $Manifest) { if (-not (Test-Path -LiteralPath $file)) { throw "Arquivo para a Release não encontrado: $file" } }

Push-Location $root
$notesFile = Join-Path ([IO.Path]::GetTempPath()) ("WinStatusBar-notes-" + [guid]::NewGuid().ToString('N') + '.md')
try {
    $remote = Invoke-Native git @('ls-remote', '--tags', 'origin', "refs/tags/$tag") 'Consulta das versões publicadas (git ls-remote)'
    if ($remote) { throw "A versão $Version já foi publicada; suba a versão em Directory.Build.props." }

    # Notas da Release (inglês): seção da versão no CHANGELOG.en.md + como instalar.
    $english = [IO.File]::ReadAllText((Join-Path $root 'CHANGELOG.en.md'))
    $section = [regex]::Match($english, "(?ms)^## \[$([regex]::Escape($Version))\][^\r\n]*\r?\n(.*?)(?=^## \[|\z)")
    if (-not $section.Success -or -not $section.Groups[1].Value.Trim()) { throw "Falta a seção [$Version] no CHANGELOG.en.md." }
    $install = @'
## Install
- **winget** (no SmartScreen prompt): `winget install SidneyJunior.WinStatusBar`
- **Installer**: download `WinStatusBar-Setup.exe` below and run it. Windows may show a SmartScreen prompt the first time (More info › Run anyway), because the installer is not code-signed yet.

Existing installations update automatically and silently.
'@
    [IO.File]::WriteAllText($notesFile, $section.Groups[1].Value.Trim() + "`n`n" + $install, (New-Object Text.UTF8Encoding $false))

    # Código: commit de tudo que não está no .gitignore, depois da busca de segredos.
    Invoke-Native git @('add', '-A') 'git add' | Out-Null
    & (Join-Path $root 'tools\github\verificar-segredos.ps1')
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    & git diff --cached --quiet; $hasChanges = $LASTEXITCODE -ne 0
    $ErrorActionPreference = $old
    if ($hasChanges) { Invoke-Native git @('commit', '-q', '-m', "Release $tag (build $Build)") 'git commit' | Out-Null }
    Invoke-Native git @('tag', '-a', $tag, '-m', "WinStatusBar $Version") 'git tag' | Out-Null
    Invoke-Native git @('push', 'origin', 'main') 'git push (main)' | Out-Null
    Invoke-Native git @('push', 'origin', $tag) "git push ($tag)" | Out-Null
    Write-Host "Código enviado: main e $tag"

    # Release: rascunho → EXE → manifesto → publicada como a mais recente.
    Invoke-Native $gh @('release', 'create', $tag, '--repo', $repo, '--draft', '--verify-tag', '--title', "WinStatusBar $Version", '--notes-file', $notesFile) 'Criação da Release' | Out-Null
    Invoke-Native $gh @('release', 'upload', $tag, $Setup, '--repo', $repo, '--clobber') 'Envio do instalador' | Out-Null
    Invoke-Native $gh @('release', 'upload', $tag, $Manifest, '--repo', $repo, '--clobber') 'Envio do manifesto' | Out-Null
    Invoke-Native $gh @('release', 'edit', $tag, '--repo', $repo, '--draft=false', '--latest') 'Publicação da Release' | Out-Null

    # Conferência pelo mesmo link fixo que os PCs usam.
    $seen = $null
    for ($i = 0; $i -lt 6; $i++) {
        try { $seen = (Invoke-RestMethod -Uri $latestManifest -Headers @{ 'Cache-Control' = 'no-cache' } -UseBasicParsing).Build } catch { }
        if ($seen -eq $Build) { break }
        Start-Sleep -Seconds 10
    }
    if ($seen -ne $Build) { throw "A Release foi criada, mas o link fixo ainda não mostra a compilação $Build (mostra: $seen)." }
    Write-Host "Publicado no GitHub: $tag (compilação $Build) — https://github.com/$repo/releases/tag/$tag"
} finally {
    Pop-Location
    Remove-Item -LiteralPath $notesFile -Force -ErrorAction SilentlyContinue
}
