# Pacote MSIX para a Microsoft Store: testes → coletor → app empacotado → Instalador\Store\*.msix
# Uso (valores do Partner Center › Identidade do produto):
#   powershell -File tools\build-msix.ps1 -IdentityName "12345Nome.WinStatusBar" -Publisher "CN=XXXXXXXX-..." -PublisherDisplayName "Seu nome"
# Sem parâmetros usa a identidade oficial reservada no Partner Center por Junior Mendes.
param(
    [string]$IdentityName = 'JuniorMendes.WinStatusBar',
    [string]$Publisher = 'CN=9FBF1CB5-AD0B-451F-B174-C8D6B7F4A349',
    [string]$PublisherDisplayName = 'Junior Mendes',
    [string]$Version,
    [switch]$SkipTests,
    [switch]$KeepIntermediates
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'
$art = Join-Path $root 'artifacts\msix'
$col = Join-Path $art 'collector'
$out = Join-Path $art 'out'

if (-not $Version) {
    $v = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
    $Version = "$v.0"   # a Store exige o 4º número (revisão) = 0
}
if ($Version -notmatch '^\d+\.\d+\.\d+\.0$') { throw "Versão inválida para a Store: $Version (use a.b.c.0)" }

Remove-Item $art -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $col, $out | Out-Null

if (-not $SkipTests) {
    Write-Host '== Testes'
    & $dotnet test (Join-Path $root 'tests\WinStatusBar.Tests\WinStatusBar.Tests.csproj') -c Release -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'Testes falharam' }
}

Write-Host '== Manifesto'
$manifest = Join-Path $art 'Package.appxmanifest'
[xml]$x = Get-Content (Join-Path $root 'src\WinStatusBar\Package\Package.appxmanifest') -Encoding UTF8
$x.Package.Identity.Name = $IdentityName
$x.Package.Identity.Publisher = $Publisher
$x.Package.Identity.Version = $Version
$x.Package.Properties.PublisherDisplayName = $PublisherDisplayName
$x.Save($manifest)

Write-Host '== Publish Collector'
& $dotnet publish (Join-Path $root 'src\WinStatusBar.Collector\WinStatusBar.Collector.csproj') -c Release -r win-x64 --self-contained true -o $col -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Publish do coletor falhou' }

Write-Host '== Pacote MSIX'
# Barra final dupla: o MSBuild exige AppxPackageDir terminando em separador.
& $dotnet publish (Join-Path $root 'src\WinStatusBar\WinStatusBar.csproj') -c Release -r win-x64 --self-contained true -nologo -v q `
    -p:PublishReadyToRun=true -p:WindowsPackageType=MSIX "-p:WsbCollectorDir=$col" "-p:WsbAppxManifest=$manifest" "-p:AppxPackageDir=$out\\"
if ($LASTEXITCODE -ne 0) { throw 'Geração do MSIX falhou' }

$msix = Get-ChildItem $out -Recurse -Filter *.msix | Select-Object -First 1
if (-not $msix) { throw 'Nenhum .msix foi gerado' }
$final = Join-Path $root 'Instalador\Store'
New-Item -ItemType Directory -Force $final | Out-Null
Get-ChildItem $final -Filter *.msix -ErrorAction SilentlyContinue | Remove-Item -Force
$target = Join-Path $final ("WinStatusBar_{0}_x64.msix" -f $Version)
Copy-Item $msix.FullName $target -Force
Write-Host ("MSIX: {0} ({1:N1} MB) · {2} · {3}" -f $target, ((Get-Item $target).Length / 1MB), $IdentityName, $Publisher)

if (-not $KeepIntermediates) {
    Remove-Item $art -Recurse -Force -ErrorAction SilentlyContinue
    Get-ChildItem (Join-Path $root 'src'), (Join-Path $root 'tests') -Directory -Recurse -Force |
        Where-Object { $_.Name -in 'bin', 'obj' } | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    if (-not (Get-ChildItem (Join-Path $root 'artifacts') -Force -ErrorAction SilentlyContinue)) { Remove-Item (Join-Path $root 'artifacts') -Force -ErrorAction SilentlyContinue }
    Write-Host 'Intermediários removidos (bin, obj, artifacts).'
}
