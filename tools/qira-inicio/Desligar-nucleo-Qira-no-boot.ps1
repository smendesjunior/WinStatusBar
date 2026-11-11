# Qira sob demanda (11/11/2026): o núcleo do Lenovo Qira deixa de iniciar com o Windows. Parado, ele ocupava ~1,8 GB
# o tempo todo; agora a barra (e as IAs no Codex) sobem o núcleo no primeiro pedido e o encerram depois da resposta.
# Remove a entrada HKCU Run "LenovoQiraNucleo" criada por Ligar-nucleo-Qira-no-boot.ps1.
# Com -Parar, encerra também o Qira que estiver aberto agora: TODAS as partes juntas (encerrar uma só trava o resto).
# Rode pelo .cmd (Explorer), fora do pacote do Claude/Codex, para gravar no HKCU real.
param([switch]$Parar)
$out = Join-Path $PSScriptRoot 'resultado.txt'
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$name = 'LenovoQiraNucleo'
$root = Join-Path $env:ProgramFiles 'Lenovo\Lenovo Qira'
$lines = @()
try {
    if (Get-ItemProperty $key -Name $name -ErrorAction SilentlyContinue) {
        Remove-ItemProperty $key -Name $name
        $lines += "Entrada $name removida do início do Windows."
    } else { $lines += "Entrada $name já não existia." }
    $approved = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
    if ((Test-Path $approved) -and (Get-ItemProperty $approved -Name $name -ErrorAction SilentlyContinue)) { Remove-ItemProperty $approved -Name $name }
    $parts = @(Get-Process | Where-Object { $_.Path -and $_.Path.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) })
    $mb = [math]::Round((($parts | Measure-Object WorkingSet64 -Sum).Sum) / 1MB)
    if ($Parar -and $parts.Count -gt 0) {
        $parts | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
        $left = @(Get-Process | Where-Object { $_.Path -and $_.Path.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase) }).Count
        $lines += "Qira encerrado: $($parts.Count) processos, $mb MB liberados (restaram $left)."
    } else { $lines += "Qira em execução agora: $($parts.Count) processos, $mb MB." }
    $still = (Get-ItemProperty $key -Name $name -ErrorAction SilentlyContinue).$name
    ("OK $(Get-Date -Format s)`r`n" + ($lines -join "`r`n") + "`r`nRun agora: " + $(if ($still) { $still } else { '(sem entrada)' })) | Out-File $out -Encoding utf8
} catch {
    "ERRO $(Get-Date -Format s): $($_.Exception.Message)" | Out-File $out -Encoding utf8
}
