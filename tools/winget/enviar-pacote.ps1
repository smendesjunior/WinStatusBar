# Primeiro envio do pacote WinStatusBar.WinStatusBar ao winget, com os manifestos da versão atual (Directory.Build.props).
# Roda fora do pacote do Claude (enviar-pacote.cmd via explorer.exe). Registro: publicar-winget.log, nesta pasta.
$log = Join-Path $PSScriptRoot 'publicar-winget.log'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
try {
    $version = (Select-Xml -Path (Join-Path $root 'Directory.Build.props') -XPath '//Version').Node.InnerText
    $dir = Join-Path $PSScriptRoot "manifests\w\WinStatusBar\WinStatusBar\$version"
    if (-not (Test-Path -LiteralPath $dir)) { throw "Manifestos da versão $version não encontrados em $dir" }
    "$(Get-Date -Format s) Enviando WinStatusBar.WinStatusBar $version ($dir)" | Set-Content $log -Encoding UTF8
    & (Join-Path $PSScriptRoot 'publicar-winget.ps1') -ManifestDir $dir *>&1 | ForEach-Object { "$_" } | Add-Content $log -Encoding UTF8
    'CONCLUÍDO' | Add-Content $log -Encoding UTF8
} catch {
    "ERRO: $($_.Exception.Message)" | Add-Content $log -Encoding UTF8
}
