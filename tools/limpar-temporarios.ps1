# Executar manualmente no PowerShell. Remove somente saídas regeneráveis e temporários identificados.
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent)).TrimEnd('\')
if ($projectRoot -ne 'C:\Users\smendesjunior\OneDrive\Aplicativos IA\WinStatusBar') { throw 'Workspace inesperado.' }
# Não apagar os intermediários antes de verificar a entrega final.
$manifest = Get-Content -LiteralPath (Join-Path $projectRoot 'Instalador\WinStatusBar-update.json') -Raw | ConvertFrom-Json
$installer = Get-Item -LiteralPath (Join-Path $projectRoot 'Instalador\WinStatusBar-Setup.exe')
if ($installer.Length -ne $manifest.Size -or (Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256).Hash -ne $manifest.Sha256) { throw 'Instalador final inválido.' }
$relativePaths = @(
 'artifacts',
 'src\WinStatusBar\bin','src\WinStatusBar\obj',
 'src\WinStatusBar.Collector\bin','src\WinStatusBar.Collector\obj',
 'src\WinStatusBar.Core\bin','src\WinStatusBar.Core\obj',
 'src\WinStatusBar.Automation\bin','src\WinStatusBar.Automation\obj',
 'src\WinStatusBar.Sensors\bin','src\WinStatusBar.Sensors\obj',
 'src\WinStatusBar.Setup\bin','src\WinStatusBar.Setup\obj',
 'tests\WinStatusBar.Tests\bin','tests\WinStatusBar.Tests\obj',
 'Instalador\Store',
 'docs\visual-audit-2026-11-06',
 'docs\visual-audit-winstatusbar-2026-11-06\baseline',
 'docs\visual-audit-winstatusbar-2026-11-06\icons8-reference',
 'docs\visual-audit-winstatusbar-2026-11-06\audit.py',
 'docs\visual-audit-winstatusbar-2026-11-06\baseline-manifest.json',
 'docs\visual-audit-winstatusbar-2026-11-06\battery-preview.svg',
 'docs\visual-audit-winstatusbar-2026-11-06\FILA.md',
 'docs\visual-audit-winstatusbar-2026-11-06\tamanho-antes.txt'
)
$verifiedPaths = foreach ($relative in $relativePaths) {
 $full = [IO.Path]::GetFullPath((Join-Path $projectRoot $relative))
 if (-not $full.StartsWith($projectRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Fora do projeto: $full" }
 if (Test-Path -LiteralPath $full) {
  if ((Resolve-Path -LiteralPath $full).Path -ne $full) { throw "Caminho divergente: $full" }
  if ((Get-Item -LiteralPath $full -Force).LinkType) { throw "Link não permitido: $full" }
  $full
 }
}
$before = (Get-ChildItem -LiteralPath $projectRoot -Recurse -File -Force | Measure-Object Length -Sum).Sum
foreach ($full in $verifiedPaths) { Write-Host "Removendo $full"; Remove-Item -LiteralPath $full -Recurse -Force }
$after = (Get-ChildItem -LiteralPath $projectRoot -Recurse -File -Force | Measure-Object Length -Sum).Sum
[pscustomobject]@{BeforeBytes=$before;AfterBytes=$after;FreedBytes=($before-$after);Removed=$verifiedPaths} | ConvertTo-Json -Depth 3 | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $projectRoot 'docs\visual-audit-winstatusbar-2026-11-06\limpeza.json')
Write-Host ('Liberados {0:N1} MB. Pasta final: {1:N1} MB.' -f (($before-$after)/1MB),($after/1MB))
