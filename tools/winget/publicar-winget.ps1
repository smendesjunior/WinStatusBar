# Pacote WinStatusBar.WinStatusBar (moniker winstatusbar) no repositório oficial do winget (microsoft/winget-pkgs).
#   -ManifestDir <pasta>  primeiro envio: manifestos escritos e validados (winget validate) nessa pasta
#   -Version X.Y.Z        atualização: wingetcreate update com o instalador da Release vX.Y.Z do GitHub
# Usa o login do GitHub CLI (token só na hora, nunca gravado). Chamado pelo build-release.ps1 em versões MINOR/MAJOR.
param([string]$Version, [string]$ManifestDir)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\github\comum.ps1')
$id = 'WinStatusBar.WinStatusBar'
$gh = Find-Gh
if (-not $gh) { throw 'GitHub CLI não encontrado. Rode tools\github\configurar-github.cmd.' }
$wingetCreate = (Get-Command wingetcreate -ErrorAction SilentlyContinue).Source
if (-not $wingetCreate) { throw 'wingetcreate não encontrado. Rode tools\github\configurar-github.cmd.' }
$token = (& $gh auth token).Trim()
if (-not $token) { throw 'GitHub CLI sem login.' }
try {
    if ($ManifestDir) {
        $out = Invoke-Native 'winget' @('validate', '--manifest', $ManifestDir) 'Validação dos manifestos'
        $versionLine = Select-String -Path (Join-Path $ManifestDir "$id.yaml") -Pattern '^PackageVersion:\s*(.+)$'
        $packageVersion = $versionLine.Matches[0].Groups[1].Value.Trim()
        $out = Invoke-Native $wingetCreate @('submit', '--no-open', '--prtitle', "New package: $id version $packageVersion", '--token', $token, $ManifestDir) 'Envio ao winget (wingetcreate submit)'
    } elseif ($Version) {
        $url = "https://github.com/smendesjunior/WinStatusBar/releases/download/v$Version/WinStatusBar-Setup.exe"
        $out = Invoke-Native $wingetCreate @('update', $id, '--version', $Version, '--urls', $url,
            '--release-notes-url', "https://github.com/smendesjunior/WinStatusBar/releases/tag/v$Version",
            '--submit', '--no-open', '--prtitle', "Update $id to $Version", '--token', $token) 'Atualização no winget (wingetcreate update)'
    } else { throw 'Informe -ManifestDir (primeiro envio) ou -Version (atualização).' }
    $pr = @($out | Select-String -Pattern 'https://github\.com/microsoft/winget-pkgs/pull/\d+' -AllMatches | ForEach-Object { $_.Matches.Value })[0]
    Write-Host ("Pedido do winget enviado: " + $(if ($pr) { $pr } else { ($out -join ' ') }))
} catch {
    throw ($_.Exception.Message -replace [regex]::Escape($token), '***')
} finally { $token = $null }
