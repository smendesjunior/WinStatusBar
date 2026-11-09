# Roda DENTRO do Windows Sandbox (LogonCommand do teste-instalador.wsb). A cada 3 s grava em C:\Saida (pasta mapeada,
# lida pelo PC de fora) o estado da instalação e da atualização, e fotografa as janelas do instalador e das Configurações
# (PrintWindow: só a janela) para conferência e para o README. Para sozinho depois de 3 horas.
$ErrorActionPreference = 'Continue'
$out = 'C:\Saida'
New-Item -ItemType Directory -Force $out | Out-Null
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class MS {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
'@
[void][MS]::SetProcessDPIAware()
$md5 = [Security.Cryptography.MD5]::Create()
$lastHash = @{}

function Save-Bitmap($bmp, [string]$tag) {
    # Só grava quando a imagem muda (evita centenas de fotos iguais).
    $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    $hash = [BitConverter]::ToString($md5.ComputeHash($ms.ToArray()))
    if ($lastHash[$tag] -ne $hash) {
        $lastHash[$tag] = $hash
        [IO.File]::WriteAllBytes((Join-Path $out ("$tag-" + (Get-Date).ToString('HHmmss') + '.png')), $ms.ToArray())
    }
    $ms.Dispose()
}
function Shot-Window([IntPtr]$h, [string]$tag) {
    $r = New-Object MS+RECT; [void][MS]::GetWindowRect($h, [ref]$r)
    $w = $r.R - $r.L; $hh = $r.B - $r.T
    if ($w -le 0 -or $hh -le 0) { return }
    $b = New-Object Drawing.Bitmap $w, $hh; $g = [Drawing.Graphics]::FromImage($b); $dc = $g.GetHdc()
    [void][MS]::PrintWindow($h, $dc, 2); $g.ReleaseHdc($dc); $g.Dispose(); Save-Bitmap $b $tag; $b.Dispose()
}

$start = Get-Date; $tick = 0
while ((Get-Date) - $start -lt [TimeSpan]::FromHours(3)) {
    $l = New-Object Collections.Generic.List[string]
    $l.Add("agora: $(Get-Date -Format s)")
    Get-Process | Where-Object { $_.ProcessName -match 'WinStatusBar|Setup|Webview2Setup' } | ForEach-Object {
        $l.Add("processo: $($_.ProcessName) pid=$($_.Id) inicio=$($_.StartTime.ToString('HH:mm:ss')) janela='$($_.MainWindowTitle)'")
    }
    $dll = Join-Path $env:LOCALAPPDATA 'Programs\WinStatusBar\WinStatusBar.dll'
    if (Test-Path $dll) { $l.Add("dll: $((Get-Item $dll).VersionInfo.ProductVersion)") }
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\WinStatusBar'
    if (Test-Path $key) { $p = Get-ItemProperty $key; $l.Add("registro: $($p.DisplayVersion) / $($p.Publisher)") }
    foreach ($wv in 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'HKCU:\Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}') {
        if (Test-Path $wv) { $l.Add("webview2: $((Get-ItemProperty $wv).pv) ($wv)") }
    }
    Get-ChildItem (Join-Path $env:LOCALAPPDATA 'WinStatusBar\Updates') -ErrorAction SilentlyContinue | ForEach-Object { $l.Add("updates: $($_.Name) $($_.Length)") }
    $log = Join-Path $env:LOCALAPPDATA 'WinStatusBar\winstatusbar.log'
    if (Test-Path $log) { Get-Content $log -Tail 400 -Encoding UTF8 | Where-Object { $_ -match 'Atualiza|Iniciado|Encerrando|WebView' } | Select-Object -Last 20 | ForEach-Object { $l.Add("log: $_") } }
    $setup = Join-Path $env:LOCALAPPDATA 'WinStatusBar\setup.log'
    if (Test-Path $setup) { $l.Add('--- setup.log'); Get-Content $setup -Encoding UTF8 | ForEach-Object { $l.Add($_) } }
    $l | Set-Content (Join-Path $out 'estado.txt') -Encoding UTF8

    foreach ($p in Get-Process | Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle -match 'Instalar o WinStatusBar|Configura' }) {
        Shot-Window $p.MainWindowHandle $(if ($p.MainWindowTitle -match 'Instalar') { 'instalador' } else { 'configuracoes' })
    }
    # Tela inteira a cada ~30 s (mostra a barra no topo).
    if ($tick % 10 -eq 0) {
        $s = [Windows.Forms.Screen]::PrimaryScreen.Bounds 2>$null
        if (-not $s) { Add-Type -AssemblyName System.Windows.Forms; $s = [Windows.Forms.Screen]::PrimaryScreen.Bounds }
        $b = New-Object Drawing.Bitmap $s.Width, $s.Height; $g = [Drawing.Graphics]::FromImage($b)
        $g.CopyFromScreen($s.X, $s.Y, 0, 0, $b.Size); $g.Dispose(); Save-Bitmap $b 'tela'; $b.Dispose()
    }
    $tick++
    Start-Sleep -Seconds 3
}
