# Espera (até 3 min) uma janela do Explorador em primeiro plano e maximizada e fotografa a tela (README).
# Fora do pacote do Claude (capture-quando-na-frente.cmd via explorer.exe). Foto em tools\readme\raw\explorer.png.
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type @'
using System; using System.Runtime.InteropServices; using System.Text;
public static class QF {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
  public static bool Cloaked(IntPtr h) { int v; return DwmGetWindowAttribute(h, 14, out v, 4) == 0 && v != 0; }
}
'@
[void][QF]::SetProcessDPIAware()
$log = Join-Path $PSScriptRoot 'capture-integracao.txt'
$deadline = (Get-Date).AddMinutes(3)
while ((Get-Date) -lt $deadline) {
    $h = [QF]::GetForegroundWindow(); $c = New-Object Text.StringBuilder 64; [void][QF]::GetClassName($h, $c, 64)
    if ($c.ToString() -eq 'CabinetWClass' -and [QF]::IsZoomed($h) -and -not [QF]::Cloaked($h)) {
        Start-Sleep -Milliseconds 1500
        $b = [Windows.Forms.Screen]::PrimaryScreen.Bounds
        $bmp = New-Object Drawing.Bitmap $b.Width, $b.Height; $g = [Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($b.X, $b.Y, 0, 0, $bmp.Size); $g.Dispose()
        $bmp.Save((Join-Path $PSScriptRoot 'raw\explorer.png')); $bmp.Dispose()
        Add-Content $log "$(Get-Date -Format HH:mm:ss) foto: explorer (janela do Explorador na frente)" -Encoding UTF8
        Add-Content $log 'FIM' -Encoding UTF8
        exit
    }
    Start-Sleep -Milliseconds 500
}
Add-Content $log 'ERRO: nenhuma janela do Explorador maximizada na frente em 3 min' -Encoding UTF8
