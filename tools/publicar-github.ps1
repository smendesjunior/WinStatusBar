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

. (Join-Path $PSScriptRoot 'github\comum.ps1')

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
    $notes = Get-ReleaseNotes $root $Version
    [IO.File]::WriteAllText($notesFile, $notes, (New-Object Text.UTF8Encoding $false))

    # Código: commit de tudo que não está no .gitignore, depois da busca de segredos no que foi preparado E nas linhas
    # adicionadas pelos commits ainda não enviados (inclusive nomes internos).
    Invoke-Native git @('add', '-A') 'git add' | Out-Null
    Invoke-Native git @('fetch', 'origin', 'main') 'git fetch' | Out-Null
    & (Join-Path $root 'tools\github\verificar-segredos.ps1')
    & (Join-Path $root 'tools\github\verificar-segredos.ps1') -Range 'origin/main..HEAD'
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    & git diff --cached --quiet; $hasChanges = $LASTEXITCODE -ne 0
    $ErrorActionPreference = $old
    if ($hasChanges) { Invoke-Native git @('commit', '-q', '-m', "Release $tag (build $Build)") 'git commit' | Out-Null }
    Invoke-Native git @('tag', '-a', $tag, '-m', "WinStatusBar $Version") 'git tag' | Out-Null
    try {
        Invoke-Native git @('push', 'origin', 'main') 'git push (main)' | Out-Null
        Invoke-Native git @('push', 'origin', $tag) "git push ($tag)" | Out-Null
    } catch {
        # O envio falhou: a etiqueta criada acima existe só neste PC. Sem apagá-la, o próximo build-release recusaria
        # esta versão como "já publicada". O erro do envio segue adiante.
        $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        & git tag -d $tag *> $null; $tagRemoved = $LASTEXITCODE -eq 0
        $ErrorActionPreference = $old
        if (-not $tagRemoved) { Write-Warning "Não foi possível apagar a etiqueta local $tag; apague com: git tag -d $tag" }
        throw
    }
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
    # A Release já está publicada neste ponto: a demora do link fixo é só um aviso (não deixa a publicação como pendente).
    if ($seen -ne $Build) { Write-Warning "A Release foi publicada, mas o link fixo ainda não mostra a compilação $Build (mostra: $seen). Confira depois: $latestManifest" }
    Write-Host "Publicado no GitHub: $tag (compilação $Build) — https://github.com/$repo/releases/tag/$tag"
} finally {
    Pop-Location
    Remove-Item -LiteralPath $notesFile -Force -ErrorAction SilentlyContinue
}
