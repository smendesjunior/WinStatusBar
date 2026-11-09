# Build de release completo: testes → publish (self-contained, x64) → payload.zip → Instalador\WinStatusBar-Setup.exe
# Uso: powershell -File tools\build-release.ps1 [-KeepIntermediates] [-SkipNetwork]
# -SkipNetwork: gera e entrega em Instalador e no OneDrive CGM, sem publicar no compartilhamento rede.
param([switch]$KeepIntermediates, [switch]$SkipNetwork)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'
$updateBuild = [DateTime]::UtcNow.ToString('yyyyMMddHHmmss')
$art = Join-Path $root ('artifacts\build-' + $updateBuild)
$app = Join-Path $art 'publish\WinStatusBar'
$col = Join-Path $art 'publish\collector'
$sensors = Join-Path $art 'publish\sensors'
$payload = Join-Path $art 'payload.zip'
$inst = Join-Path $art 'installer'
# Cada compilação usa uma pasta nova; -KeepIntermediates também preserva builds anteriores.
New-Item -ItemType Directory -Force $app, $col, $inst | Out-Null

Write-Host '== Testes'
& $dotnet test (Join-Path $root 'tests\WinStatusBar.Tests\WinStatusBar.Tests.csproj') -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Testes falharam' }

Write-Host '== Publish WinStatusBar'
& $dotnet publish (Join-Path $root 'src\WinStatusBar\WinStatusBar.csproj') -c Release -r win-x64 --self-contained true -o $app -nologo -v q -p:PublishReadyToRun=true "-p:NetworkUpdateBuild=$updateBuild"
if ($LASTEXITCODE -ne 0) { throw 'Publish do app falhou' }

Write-Host '== Publish Collector'
& $dotnet publish (Join-Path $root 'src\WinStatusBar.Collector\WinStatusBar.Collector.csproj') -c Release -r win-x64 --self-contained true -o $col -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Publish do coletor falhou' }
# O coletor compartilha o runtime self-contained do app; copia apenas os arquivos próprios.
Get-ChildItem $col -Filter 'WinStatusBar.Collector*' | Copy-Item -Destination $app -Force

Write-Host '== Publish Sensors'
& $dotnet publish (Join-Path $root 'src\WinStatusBar.Sensors\WinStatusBar.Sensors.csproj') -c Release -r win-x64 --self-contained true -o $sensors -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Publish do leitor de sensores falhou' }
New-Item -ItemType Directory -Force (Join-Path $app 'Sensors') | Out-Null
Copy-Item (Join-Path $sensors '*') (Join-Path $app 'Sensors') -Recurse -Force
Remove-Item (Join-Path $app 'Sensors\*.pdb') -Force -ErrorAction SilentlyContinue

Remove-Item (Join-Path $app '*.pdb') -Force -ErrorAction SilentlyContinue
Write-Host '== Publish Automation'
$automation = Join-Path $art 'publish\automation'
& $dotnet publish (Join-Path $root 'src\WinStatusBar.Automation\WinStatusBar.Automation.csproj') -c Release -r win-x64 --self-contained true -o $automation -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Publish do auxiliar de controle falhou' }
# IAs da barra no Codex (encaminhador da porta 11435): mesma pasta do auxiliar de controle, aproveitando o .NET de lá.
Write-Host '== Publish IAs no Codex'
& $dotnet publish (Join-Path $root 'src\WinStatusBar.CodexIAs\WinStatusBar.CodexIAs.csproj') -c Release -r win-x64 --self-contained true -o $automation -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Publish das IAs no Codex falhou' }
if (-not (Test-Path (Join-Path $automation 'WinStatusBar.CodexIAs.exe'))) { throw 'WinStatusBar.CodexIAs.exe ausente no publish' }
Remove-Item (Join-Path $automation '*.pdb') -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force (Join-Path $app 'Automation') | Out-Null
Copy-Item (Join-Path $automation '*') (Join-Path $app 'Automation') -Recurse -Force
Write-Host '== Payload'
# Compactação máxima do .NET (Deflate SmallestSize), melhor que Compress-Archive Optimal.
& $dotnet run (Join-Path $root 'tools\make-payload.cs') -- $app $payload
if ($LASTEXITCODE -ne 0) { throw 'Falha ao gerar o payload' }

Write-Host '== Instalador'
& $dotnet publish (Join-Path $root 'src\WinStatusBar.Setup\WinStatusBar.Setup.csproj') -c Release -r win-x64 -o $inst -nologo -v q "-p:PayloadZip=$payload"
if ($LASTEXITCODE -ne 0) { throw 'Publish do instalador falhou' }
Remove-Item (Join-Path $inst '*.pdb') -Force -ErrorAction SilentlyContinue

$setup = Get-Item (Join-Path $inst 'WinStatusBar.Setup.exe')
$appSize = (Get-ChildItem $app -Recurse | Measure-Object Length -Sum).Sum
Write-Host ("Aplicativo: {0} ({1:N1} MB)" -f $app, ($appSize / 1MB))

# Entrega final: um único arquivo em Instalador\ (o que vai para outros PCs)
$final = Join-Path $root 'Instalador'
New-Item -ItemType Directory -Force $final | Out-Null
$target = Join-Path $final 'WinStatusBar-Setup.exe'
Copy-Item $setup.FullName $target -Force
Write-Host ("Instalador: {0} ({1:N1} MB)" -f $target, ((Get-Item $target).Length / 1MB))
$manifestPath = Join-Path $final 'WinStatusBar-update.json'
@{ Build = $updateBuild; Sha256 = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash; Size = (Get-Item -LiteralPath $target).Length } |
    ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding UTF8
# Entrega única em Instalador: este PC atualiza daqui (padronizado em 07/11/2026). Outros PCs: compartilhamento rede
# ou a pasta do OneDrive CGM (PCs com essa conta sincronizada; o BAT de lá grava essa pasta como fonte de atualização).
$cgmDir = Join-Path $env:USERPROFILE 'OneDrive - Empresa\WinStatusBar'
if (Test-Path -LiteralPath (Split-Path $cgmDir -Parent)) {
    New-Item -ItemType Directory -Force $cgmDir | Out-Null
    # Mesma ordem da rede: EXE completo primeiro, manifesto por último.
    Copy-Item -LiteralPath $target -Destination (Join-Path $cgmDir 'WinStatusBar-Setup.exe') -Force
    Copy-Item -LiteralPath (Join-Path $final 'Instalar-WinStatusBar.bat') -Destination (Join-Path $cgmDir 'Instalar-WinStatusBar.bat') -Force
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $cgmDir 'WinStatusBar-update.json') -Force
    Write-Host "Atualização publicada no OneDrive CGM: $cgmDir"
}
# Publicação: arquivo completo primeiro; manifesto por último. Nunca anuncia uma cópia em andamento.
$networkDir = '\\servidor\rede\Obras\Engenharia\WinStatusBar'
try {
    if ($SkipNetwork) { throw 'publicação na rede adiada (-SkipNetwork)' }
    if (-not (Test-Path -LiteralPath $networkDir)) { throw 'Compartilhamento indisponível' }
    $networkSetup = Join-Path $networkDir 'WinStatusBar-Setup.exe'
    $networkManifest = Join-Path $networkDir 'WinStatusBar-update.json'
    # O compartilhamento permite gravar, mas restringe substituição por rename.
    # Tamanho/hash rejeitam cópias parciais; publica o manifesto por último.
    Copy-Item -LiteralPath $target -Destination $networkSetup -Force -ErrorAction Stop
    Copy-Item -LiteralPath $manifestPath -Destination $networkManifest -Force -ErrorAction Stop
    Copy-Item -LiteralPath (Join-Path $final 'Instalar-WinStatusBar.bat') -Destination (Join-Path $networkDir 'Instalar-WinStatusBar.bat') -Force -ErrorAction Stop
    Write-Host "Atualização publicada na rede: $updateBuild"
} catch {
    Write-Warning "Instalador gerado, mas não publicado na rede: $($_.Exception.Message). Copie o EXE e WinStatusBar-update.json de Instalador para $networkDir."
}

# Limpeza (determinação de 07/11/2026): a pasta fica só com os arquivos finais. Remove este build e os anteriores
# (artifacts inteiro), bin/obj e saídas de diagnóstico; o instalador final já está em Instalador.
if (-not $KeepIntermediates) {
    & $dotnet build-server shutdown 2>&1 | Out-Null
    Remove-Item (Join-Path $root 'artifacts') -Recurse -Force -ErrorAction SilentlyContinue
    Get-ChildItem (Join-Path $root 'src'), (Join-Path $root 'tests') -Directory -Recurse -Force |
        Where-Object { $_.Name -in 'bin', 'obj' } | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    Get-ChildItem (Join-Path $root 'tools\diag') -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -in '.txt', '.log' } | Remove-Item -Force -ErrorAction SilentlyContinue
    Write-Host 'Temporários e builds anteriores removidos (artifacts, bin, obj, saídas de diagnóstico).'
}
