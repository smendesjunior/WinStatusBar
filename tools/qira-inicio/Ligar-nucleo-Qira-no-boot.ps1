# Faz o núcleo do Lenovo Qira iniciar junto com o Windows: ProcessHub em modo -AutoStart (modo do próprio Qira).
# Sobe LenovoQiraCore, serviço de modelo (pfm), mcpMgmtService e conectores, sem abrir a janela do Qira.
# A barra abre a interface (QuantumAI.Hub) sozinha quando você usa o Qira (testado em 09/11/2026: respondeu em 6,9 s).
# Obs.: "Lenovo Qira.Launcher.exe -AutoStart" NÃO serve: o iniciador fica aberto sozinho e não sobe o núcleo.
# ProcessHub.exe é programa de console: aberto direto pelo Run, o Windows Terminal mostra a janela com o log dele
# (e fechar essa janela derruba o Qira). Por isso ele roda dentro de "conhost.exe --headless", que dá a ele um
# console sem janela nenhuma (09/11/2026).
# Rode pelo .cmd (Explorer), fora do pacote do Claude/Codex, para gravar no HKCU real.
$out = Join-Path $PSScriptRoot 'resultado.txt'
$launcher = Join-Path $env:ProgramFiles 'Lenovo\Lenovo Qira\QTServer\ProcessHub.exe'
$conhost = Join-Path $env:SystemRoot 'System32\conhost.exe'
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$name = 'LenovoQiraNucleo'
$cmd = '"' + $conhost + '" --headless "' + $launcher + '" -AutoStart'
try {
    if (-not (Test-Path $launcher)) { throw "Lenovo Qira não está instalado ($launcher)." }
    if (-not (Test-Path $conhost)) { throw "conhost.exe não encontrado ($conhost)." }
    Set-ItemProperty -Path $key -Name $name -Value $cmd -Type String
    # Se alguém desativou a entrada no Gerenciador de Tarefas, reativa.
    $approved = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
    if ((Test-Path $approved) -and (Get-ItemProperty $approved -Name $name -ErrorAction SilentlyContinue)) { Remove-ItemProperty $approved -Name $name }
    $now = (Get-ItemProperty $key -Name $name).$name
    "OK $(Get-Date -Format s)`r`n$name = $now" | Out-File $out -Encoding utf8
} catch {
    "ERRO $(Get-Date -Format s): $($_.Exception.Message)" | Out-File $out -Encoding utf8
}
