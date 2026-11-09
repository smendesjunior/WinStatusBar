# Build de release completo: testes → publish (self-contained, x64) → payload.zip → instalador → entrega e publicação.
# Uso: powershell -File tools\build-release.ps1 [-KeepIntermediates] [-SandboxTest]
#   (sem opções)     entrega em Instalador (este PC se atualiza por lá) e publica no GitHub (código + Release).
#   -SandboxTest     só gera tools\sandbox\teste\WinStatusBar-Setup.exe para testar no Windows Sandbox (sem Instalador e sem GitHub).
#   -KeepIntermediates mantém artifacts, bin e obj.
param([switch]$KeepIntermediates, [switch]$SandboxTest)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'
$updateBuild = [DateTime]::UtcNow.ToString('yyyyMMddHHmmss')
$version = (Select-Xml -Path (Join-Path $root 'Directory.Build.props') -XPath '//Version').Node.InnerText
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Versão inválida em Directory.Build.props: '$version'" }
$art = Join-Path $root ('artifacts\build-' + $updateBuild)
$app = Join-Path $art 'publish\WinStatusBar'
$col = Join-Path $art 'publish\collector'
$sensors = Join-Path $art 'publish\sensors'
$payload = Join-Path $art 'payload.zip'
$inst = Join-Path $art 'installer'
# Cada compilação usa uma pasta nova; -KeepIntermediates também preserva builds anteriores.
New-Item -ItemType Directory -Force $app, $col, $inst | Out-Null
Write-Host "== WinStatusBar $version (compilação $updateBuild)"
$utf8 = New-Object Text.UTF8Encoding $false

if (-not $SandboxTest) {
    # Versão já publicada? Confere ANTES de mexer nos CHANGELOGs e de entregar em Instalador (que este PC instala).
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    $remoteTag = @(& git -C $root ls-remote --tags origin "refs/tags/v$version" 2>&1); $lsCode = $LASTEXITCODE
    $localTag = @(& git -C $root tag --list "v$version")
    $ErrorActionPreference = $old
    if ($lsCode -ne 0) { throw "Não foi possível consultar as versões publicadas no GitHub (git ls-remote): $($remoteTag -join ' ')" }
    if ($remoteTag -or $localTag) { throw "A versão $version já foi publicada. Suba a versão em Directory.Build.props e registre-a nos dois CHANGELOGs." }
}

# A compilação entra no CHANGELOG antes do publish (o app leva o CHANGELOG.md para Configurações › Atualizações).
# Só na publicação real (a versão acabou de ser conferida como nova); aceita o marcador BUILD ou o número de um build
# anterior desta mesma versão que não chegou a ser publicado. O teste do Sandbox não mexe nos CHANGELOGs.
foreach ($name in $(if ($SandboxTest) { @() } else { 'CHANGELOG.md', 'CHANGELOG.en.md' })) {
    $file = Join-Path $root $name
    if (-not (Test-Path -LiteralPath $file)) { continue }
    $text = [IO.File]::ReadAllText($file)
    $updated = [regex]::Replace($text, "(?m)^(## \[$([regex]::Escape($version))\][^\r\n]*?(?:compilação|build) )(?:BUILD|\d{14})", "`${1}$updateBuild")
    if ($updated -ne $text) { [IO.File]::WriteAllText($file, $updated, $utf8) }
}

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
if ($setup.Length -lt 50MB) { throw "Instalador pequeno demais ($($setup.Length) bytes): o pacote do app não entrou." }

if ($SandboxTest) {
    # Teste no Windows Sandbox: compilação anterior à da Release, para provar a atualização silenciosa pelo GitHub.
    $testDir = Join-Path $root 'tools\sandbox\teste'
    Remove-Item -LiteralPath $testDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $testDir | Out-Null
    Copy-Item -LiteralPath $setup.FullName -Destination (Join-Path $testDir 'WinStatusBar-Setup.exe') -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'sandbox\monitor-sandbox.ps1') -Destination $testDir -Force
    $outDir = Join-Path $root 'tools\sandbox\saida'
    Remove-Item -LiteralPath $outDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $outDir | Out-Null
    # Configuração do Sandbox gerada aqui (os caminhos são deste PC e não vão para o repositório): C:\Teste só leitura,
    # C:\Saida gravável (estado e fotos do monitor, lidos daqui de fora), rede ligada e o monitor iniciado no logon.
    $wsb = "<Configuration>`r`n  <MappedFolders>`r`n    <MappedFolder>`r`n      <HostFolder>$testDir</HostFolder>`r`n      <SandboxFolder>C:\Teste</SandboxFolder>`r`n      <ReadOnly>true</ReadOnly>`r`n    </MappedFolder>`r`n" +
           "    <MappedFolder>`r`n      <HostFolder>$outDir</HostFolder>`r`n      <SandboxFolder>C:\Saida</SandboxFolder>`r`n      <ReadOnly>false</ReadOnly>`r`n    </MappedFolder>`r`n  </MappedFolders>`r`n" +
           "  <Networking>Enable</Networking>`r`n  <LogonCommand>`r`n    <Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Minimized -File C:\Teste\monitor-sandbox.ps1</Command>`r`n  </LogonCommand>`r`n</Configuration>`r`n"
    [IO.File]::WriteAllText((Join-Path $testDir 'teste-instalador.wsb'), $wsb, $utf8)
    Write-Host "Instalador de teste (compilação $updateBuild): $testDir\WinStatusBar-Setup.exe — abra teste-instalador.wsb nessa pasta"
} else {
    # Entrega local: este PC se atualiza por Instalador (update-source.json com Mode Local).
    $final = Join-Path $root 'Instalador'
    New-Item -ItemType Directory -Force $final | Out-Null
    $target = Join-Path $final 'WinStatusBar-Setup.exe'
    Copy-Item $setup.FullName $target -Force
    Write-Host ("Instalador: {0} ({1:N1} MB)" -f $target, ((Get-Item $target).Length / 1MB))
    $manifestPath = Join-Path $final 'WinStatusBar-update.json'
    $manifest = [ordered]@{
        Version = $version
        Build   = $updateBuild
        Sha256  = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
        Size    = (Get-Item -LiteralPath $target).Length
        Url     = "https://github.com/smendesjunior/WinStatusBar/releases/download/v$version/WinStatusBar-Setup.exe"
    }
    # Manifesto por último: nunca anuncia um instalador ainda em cópia.
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json), $utf8)

    # Publicação: GitHub (código + Release). Falha não apaga a entrega local; fica como pendência no log.
    $published = $false
    try {
        & (Join-Path $PSScriptRoot 'publicar-github.ps1') -Version $version -Build $updateBuild -Setup $target -Manifest $manifestPath
        $published = $true
    } catch {
        Write-Warning "Instalador gerado em Instalador, mas a publicação no GitHub ficou pendente: $($_.Exception.Message)"
    }

    # winget (WinStatusBar.WinStatusBar): só em versão MINOR ou MAJOR. PATCH fica no GitHub (os PCs se atualizam sozinhos).
    if ($published) {
        $previous = @(git -C $root tag --list 'v*' --sort=-v:refname | Where-Object { $_ -ne "v$version" })[0]
        $minorChanged = -not $previous -or ([version]$previous.TrimStart('v')).ToString(2) -ne ([version]$version).ToString(2)
        if ($minorChanged) {
            try { & (Join-Path $PSScriptRoot 'winget\publicar-winget.ps1') -Version $version }
            catch { Write-Warning "Release publicada no GitHub, mas o pedido do winget ficou pendente: $($_.Exception.Message)" }
        } else { Write-Host "winget: versão PATCH ($previous → v$version), pacote não atualizado." }
    }
}

# Limpeza (determinação de 07/11/2026): a pasta fica só com os arquivos finais. Remove este build e os anteriores
# (artifacts inteiro), bin/obj e saídas de diagnóstico; o instalador final já está em Instalador.
if (-not $KeepIntermediates) {
    & $dotnet build-server shutdown 2>&1 | Out-Null
    Remove-Item (Join-Path $root 'artifacts') -Recurse -Force -ErrorAction SilentlyContinue
    Get-ChildItem (Join-Path $root 'src'), (Join-Path $root 'tests') -Directory -Recurse -Force |
        Where-Object { $_.Name -in 'bin', 'obj' } | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    Get-ChildItem (Join-Path $root 'tools\diag') -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -in '.txt', '.log', '.png' } | Remove-Item -Force -ErrorAction SilentlyContinue
    Write-Host 'Temporários e builds anteriores removidos (artifacts, bin, obj, saídas de diagnóstico).'
}
