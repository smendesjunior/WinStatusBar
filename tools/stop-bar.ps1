# Encerra o WinStatusBar em execução de forma limpa (sinaliza o evento nomeado; a barra remove o registro da AppBar).
# A barra termina sozinha em poucos segundos (passados 3 s, ela mesma força a saída); aqui a espera vai até 10 s.
$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$ev = $null
if ([System.Threading.EventWaitHandle]::TryOpenExisting("WinStatusBar.Exit.$sid", [ref]$ev)) { $ev.Set() | Out-Null; $ev.Dispose(); "sinalizado" } else { "nenhuma instância" }
for ($i = 0; $i -lt 40 -and (Get-Process WinStatusBar -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 250 }
if (Get-Process WinStatusBar -ErrorAction SilentlyContinue) { "ainda em execução" } else { "encerrado" }
