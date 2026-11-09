# Teste: fecha o Qira inteiro (todas as partes juntas, como o reinício da barra) e roda a entrada de
# inicialização exatamente como o Windows faria no logon. Depois lista o que subiu e confere que
# nenhuma janela do ProcessHub (console/Windows Terminal) ficou visível.
$out = Join-Path $PSScriptRoot 'simulacao.txt'
$names = 'QuantumAI.Hub','nextmove-bridge','ProcessHub','LenovoQiraCore','mcpMgmtService','Lenovo Qira.Launcher','Ludp.Listener',
         'Notification.Listener','lenovo.pfm.pipe','LocalAgent','CreatorIMG.MCPServer','CatchMeUpMcpServer','CzCloudMcpServer','file-search-service'
Add-Type -Namespace Wsb -Name Win -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
public delegate bool EnumProc(IntPtr h, IntPtr p);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
public static System.Collections.Generic.List<IntPtr> Find(string part) {
    var r = new System.Collections.Generic.List<IntPtr>();
    EnumWindows((h, p) => { if (IsWindowVisible(h)) { var sb = new System.Text.StringBuilder(512); GetWindowText(h, sb, 512);
        if (sb.ToString().IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0) r.Add(h); } return true; }, IntPtr.Zero);
    return r;
}
'@
$marca = 'Lenovo Qira\QTServer'   # título da janela de console do ProcessHub (caminho do exe)
$log = @("Início $(Get-Date -Format s)")
$log += "Janelas visíveis do ProcessHub antes: $((([Wsb.Win]::Find($marca)) | Measure-Object).Count)"
foreach ($n in $names) { Get-Process -Name $n -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch {} } }
Start-Sleep -Seconds 5
# A aba do Windows Terminal fica aberta mostrando "processo encerrado"; fecha essa janela que sobrou.
foreach ($h in [Wsb.Win]::Find($marca)) { [void][Wsb.Win]::PostMessage($h, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) }
Start-Sleep -Seconds 2
$left = @(Get-Process -Name $names -ErrorAction SilentlyContinue)
$log += "Partes ainda abertas após fechar: $($left.Count)"
$log += "Janelas do ProcessHub após fechar: $((([Wsb.Win]::Find($marca)) | Measure-Object).Count)"
$entry = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name LenovoQiraNucleo -ErrorAction SilentlyContinue).LenovoQiraNucleo
if (-not $entry) { $log += 'ERRO: entrada LenovoQiraNucleo não existe'; $log | Out-File $out -Encoding utf8; exit 1 }
$log += "Entrada: $entry"
$exe = $entry.Substring(1, $entry.IndexOf('"', 1) - 1); $args0 = $entry.Substring($entry.IndexOf('"', 1) + 1).Trim()
$wtAntes = @(Get-Process WindowsTerminal, OpenConsole -ErrorAction SilentlyContinue | ForEach-Object Id)
Start-Process -FilePath $exe -ArgumentList $args0 -WorkingDirectory (Split-Path $exe)
$visivel = 0
for ($i = 0; $i -lt 90; $i++) {
    Start-Sleep -Seconds 1
    $visivel = [Math]::Max($visivel, (([Wsb.Win]::Find($marca)) | Measure-Object).Count)
    if ((Get-Process LenovoQiraCore, 'lenovo.pfm.pipe', mcpMgmtService -ErrorAction SilentlyContinue | Select-Object -Unique ProcessName).Count -ge 3) { break }
}
for ($j = 0; $j -lt 10; $j++) { Start-Sleep -Seconds 1; $visivel = [Math]::Max($visivel, (([Wsb.Win]::Find($marca)) | Measure-Object).Count) }
$wtNovos = @(Get-Process WindowsTerminal, OpenConsole -ErrorAction SilentlyContinue | Where-Object { $_.Id -notin $wtAntes })
$log += "Após $($i + 11) s:"
$log += (Get-Process -Name $names -ErrorAction SilentlyContinue | Group-Object ProcessName | ForEach-Object { "  $($_.Name) x$($_.Count)" })
$log += "Hub (janela) aberto: $([bool](Get-Process QuantumAI.Hub -ErrorAction SilentlyContinue))"
$log += "Janelas visíveis do ProcessHub durante a partida (máximo): $visivel"
$log += "Windows Terminal/OpenConsole novos: $($wtNovos.Count)"
$log += ($(if ($visivel -eq 0 -and $wtNovos.Count -eq 0) { 'RESULTADO: OK, início invisível' } else { 'RESULTADO: FALHOU, apareceu janela' }))
$log += "Fim $(Get-Date -Format s)"
$log | Out-File $out -Encoding utf8
