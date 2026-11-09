# Capturas do README: a barra integrada à barra de título de uma janela maximizada (Codex, Explorador) e sobre a área
# de trabalho. Fora do pacote do Claude (capture-integracao.cmd via explorer.exe). Grava em tools\readme\raw (fora do git);
# o que entra no README é revisado e borrado depois. Devolve as janelas como estavam.
param([string[]]$Cenas = @('codex', 'explorer', 'desktop'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class CI {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  public static System.Collections.Generic.List<long> Windows(string cls) {
    var list = new System.Collections.Generic.List<long>();
    EnumWindows((h, l) => { var c = new System.Text.StringBuilder(256); GetClassName(h, c, 256); if (c.ToString() == cls && IsWindowVisible(h)) list.Add(h.ToInt64()); return true; }, IntPtr.Zero);
    return list;
  }
}
'@
[void][CI]::SetProcessDPIAware()
$raw = Join-Path $PSScriptRoot 'raw'
New-Item -ItemType Directory -Force $raw | Out-Null
$log = Join-Path $PSScriptRoot 'capture-integracao.txt'
$screen = [Windows.Forms.Screen]::PrimaryScreen
$b = $screen.Bounds
function Say([string]$t) { Add-Content -Path $log -Value "$(Get-Date -Format HH:mm:ss) $t" -Encoding UTF8 }
function Front([IntPtr]$h) { [CI]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero); [CI]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero); [void][CI]::SetForegroundWindow($h) }
function Shot([string]$name) {
    $bmp = New-Object Drawing.Bitmap $b.Width, $b.Height
    $g = [Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($b.X, $b.Y, 0, 0, $bmp.Size); $g.Dispose()
    $bmp.Save((Join-Path $raw "$name.png")); $bmp.Dispose(); Say "foto: $name"
}
Remove-Item $log -ErrorAction SilentlyContinue
try {
    # 1. Codex (app OpenAI.Codex, processo ChatGPT) maximizado.
    $codex = if ($Cenas -notcontains 'codex') { $null } else { Get-Process ChatGPT -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1 }
    if ($codex) {
        $h = $codex.MainWindowHandle; $wasMax = [CI]::IsZoomed($h); $wasMin = [CI]::IsIconic($h)
        [void][CI]::ShowWindow($h, 3); Front $h; Start-Sleep -Milliseconds 2500
        Shot 'codex'
        if ($wasMin) { [void][CI]::ShowWindow($h, 6) } elseif (-not $wasMax) { [void][CI]::ShowWindow($h, 9) }
    } else { Say 'Codex não está aberto' }

    # 2. Explorador numa janela NOVA em "Este Computador" (pasta padrão do Windows), por cima só durante a foto, e fechada.
    $shell = New-Object -ComObject Shell.Application
    if ($Cenas -contains 'explorer') {
        function Cabinets { @([CI]::Windows('CabinetWClass')) }
        $known = @(Cabinets)
        Start-Process explorer.exe '/n,::{20D04FE0-3AEA-1069-A2D8-08002B30309D}'
        $new = $null; $deadline = (Get-Date).AddSeconds(15)
        while (-not $new -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 400; $new = @(Cabinets | Where-Object { $known -notcontains $_ })[0] }
        if (-not $new) {
            # O Explorador reaproveitou uma janela "Este Computador" já aberta (sobra de tentativa anterior): usa essa.
            $new = @(Cabinets | Where-Object { $t = New-Object System.Text.StringBuilder 256; [void][CI]::GetWindowText([IntPtr]$_, $t, 256); $t.ToString() -like 'Este Computador*' })[0]
        }
        if ($new) {
            $h = [IntPtr]$new
            [void][CI]::ShowWindow($h, 3)
            [void][CI]::SetWindowPos($h, [IntPtr](-1), 0, 0, 0, 0, 0x0003)   # HWND_TOPMOST, sem mover nem redimensionar
            Front $h; Start-Sleep -Milliseconds 2500
            Shot 'explorer'
            [void][CI]::SetWindowPos($h, [IntPtr](-2), 0, 0, 0, 0, 0x0003)   # HWND_NOTOPMOST
            [void][CI]::PostMessage($h, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)   # WM_CLOSE
        } else { Say 'janela nova do Explorador não apareceu' }
    }

    # 3. Área de trabalho (tudo minimizado) e volta.
    if ($Cenas -contains 'desktop') {
        $shell.MinimizeAll(); Start-Sleep -Milliseconds 3000
        Shot 'desktop'
        $shell.UndoMinimizeALL()
    }
    Say 'FIM'
} catch { Say "ERRO: $($_.Exception.Message)" }
