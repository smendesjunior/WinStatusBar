# Capturas para o README (fora do pacote do Claude: capture-readme.cmd via explorer.exe).
# Abre cada painel da barra pelos botões (UI Automation), fotografa só o painel e fecha com Esc.
# -Explore: guarda as imagens brutas e a lista de textos (para decidir o que borrar). Sem -Explore: borra os dados
# pessoais (rede, IPs, dispositivos, e-mails) e grava as imagens finais em docs\screenshots.
param([switch]$Explore)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class CR {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
}
'@
[void][CR]::SetProcessDPIAware()
$A = [System.Windows.Automation.AutomationElement]; $T = [System.Windows.Automation.TreeScope]; $C = [System.Windows.Automation.ControlType]
$True_ = [System.Windows.Automation.Condition]::TrueCondition
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$raw = Join-Path $PSScriptRoot 'raw'
$final = Join-Path $root 'docs\screenshots'
New-Item -ItemType Directory -Force $raw, $final | Out-Null
$report = New-Object Collections.Generic.List[string]

function Windows() {
    $pids = @(Get-Process WinStatusBar | ForEach-Object Id)
    @($A::RootElement.FindAll($T::Children, $True_) | Where-Object { $pids -contains $_.Current.ProcessId -and -not $_.Current.IsOffscreen })
}
function Rect($el) { $r = $el.Current.BoundingRectangle; [Drawing.Rectangle]::new([int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height) }
function Grab([Drawing.Rectangle]$r) {
    $bmp = New-Object Drawing.Bitmap $r.Width, $r.Height
    $g = [Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($r.X, $r.Y, 0, 0, $r.Size); $g.Dispose(); $bmp
}
function Pixelate([Drawing.Bitmap]$bmp, [Drawing.Rectangle]$r) {
    $r = [Drawing.Rectangle]::Intersect($r, [Drawing.Rectangle]::new(0, 0, $bmp.Width, $bmp.Height))
    if ($r.Width -le 0 -or $r.Height -le 0) { return }
    $small = New-Object Drawing.Bitmap ([Math]::Max(1, [int]($r.Width / 14))), ([Math]::Max(1, [int]($r.Height / 14)))
    $g = [Drawing.Graphics]::FromImage($small); $g.DrawImage($bmp, [Drawing.Rectangle]::new(0, 0, $small.Width, $small.Height), $r, [Drawing.GraphicsUnit]::Pixel); $g.Dispose()
    $g = [Drawing.Graphics]::FromImage($bmp); $g.InterpolationMode = 'NearestNeighbor'; $g.PixelOffsetMode = 'Half'
    $g.DrawImage($small, $r, [Drawing.Rectangle]::new(0, 0, $small.Width, $small.Height), [Drawing.GraphicsUnit]::Pixel); $g.Dispose(); $small.Dispose()
}
function Esc() { [CR]::keybd_event(0x1B, 0, 0, [UIntPtr]::Zero); [CR]::keybd_event(0x1B, 0, 2, [UIntPtr]::Zero); Start-Sleep -Milliseconds 700 }

# Textos com dado pessoal: valor nas linhas destes rótulos, IPs, e-mails, MAC e o nome da rede/dispositivos.
$sensitiveLabels = '^(SSID|Nome DNS|Endere.o IPv4|Endere.o IPv6|Gateway.*|Endere.o f.sico|MAC|Servidores DNS.*|Conta|E-mail)\s*:?$'
# IPv4, IPv6 (4+ grupos, para não pegar horários), e-mail, MAC e valores em dólar (saldo e gastos da conta).
$sensitiveValue = '(\d{1,3}\.){3}\d{1,3}|([0-9a-f]{1,4}:){4,}[0-9a-f]{0,4}|@[A-Za-z0-9-]+\.|([0-9A-F]{2}[:-]){5}[0-9A-F]{2}|US\$\s?[\d.,]+'

$bar = @(Windows | Where-Object { $r = $_.Current.BoundingRectangle; $r.Y -lt 5 -and $r.Height -lt 200 -and $r.Width -gt 1000 })[0]
if (-not $bar) { throw 'Barra não encontrada' }
$barRect = Rect $bar

# 1. A barra (duas metades legíveis + inteira).
$full = Grab $barRect
$full.Save((Join-Path $raw 'bar-full.png'))
$report.Add("bar: $($barRect)")

$targets = [ordered]@{
    'network' = 'NetworkModule'; 'cpu' = 'CpuModule'; 'ram' = 'RamModule'; 'gpu' = 'GpuModule'; 'battery' = 'BatteryModule'
    'codex-quotas' = 'CodexQuotaButton'; 'claude-quotas' = 'ClaudeQuotaButton'
    'task' = 'TaskModule'; 'more' = 'MoreButton'
}
$named = @{ 'agent' = 'Agente de IA'; 'chat' = 'Pergunte' }
$all = @($bar.FindAll($T::Descendants, $True_))
$buttons = [ordered]@{}
foreach ($k in $targets.Keys) { $b = @($all | Where-Object { $_.Current.AutomationId -eq $targets[$k] })[0]; if ($b) { $buttons[$k] = $b } }
foreach ($k in $named.Keys) { $b = @($all | Where-Object { $_.Current.ControlType -eq $C::Button -and $_.Current.Name -like "$($named[$k])*" })[0]; if ($b) { $buttons[$k] = $b } }

foreach ($name in $buttons.Keys) {
    try {
        $before = @(Windows | ForEach-Object { $_.Current.NativeWindowHandle })
        $buttons[$name].GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 1600
        $popup = @(Windows | Where-Object { $before -notcontains $_.Current.NativeWindowHandle })[0]
        $scope = $popup
        if (-not $popup) {
            # Painel dentro da própria janela da barra (flyout): maior elemento abaixo da barra.
            $scope = @($bar.FindAll($T::Descendants, $True_) | Where-Object { $r = $_.Current.BoundingRectangle; $r.Y -ge $barRect.Bottom - 2 -and $r.Height -gt 60 } |
                Sort-Object { - ($_.Current.BoundingRectangle.Width * $_.Current.BoundingRectangle.Height) })[0]
        }
        if (-not $scope) { $report.Add("${name}: nenhum painel abriu"); Esc; continue }
        $pr = Rect $scope
        $shot = Grab $pr
        $texts = @($scope.FindAll($T::Descendants, $True_) | Where-Object { $_.Current.Name -and $_.Current.BoundingRectangle.Width -gt 0 })
        $report.Add("${name}: painel $pr ($(if ($popup) { 'janela' } else { 'flyout' })) textos=$($texts.Count)")
        if ($Explore) {
            $shot.Save((Join-Path $raw "$name.png"))
            foreach ($tx in $texts) { $tr = Rect $tx; $report.Add("    [$($tx.Current.ControlType.ProgrammaticName -replace 'ControlType.','')] '$($tx.Current.Name)' @ $($tr.X - $pr.X),$($tr.Y - $pr.Y) $($tr.Width)x$($tr.Height)") }
        } else {
            $blur = New-Object Collections.Generic.List[Drawing.Rectangle]
            $labels = @($texts | Where-Object { $_.Current.Name.Trim() -match $sensitiveLabels })
            foreach ($l in $labels) {
                $lr = Rect $l; $cy = $lr.Y + $lr.Height / 2
                foreach ($v in $texts) { $vr = Rect $v; if ($vr.X -gt $lr.Right -and [Math]::Abs(($vr.Y + $vr.Height / 2) - $cy) -lt 8) { $blur.Add($vr) } }
            }
            $secrets = @($texts | Where-Object { $_.Current.Name -match $sensitiveValue })
            foreach ($s in $secrets) { $blur.Add((Rect $s)) }
            # Nome da rede Wi-Fi e dispositivos Bluetooth nos botões do painel de rede.
            if ($name -eq 'network') {
                $ssid = @($labels | Where-Object { $_.Current.Name -match '^SSID' } | ForEach-Object {
                    $lr = Rect $_; $cy = $lr.Y + $lr.Height / 2
                    @($texts | Where-Object { $vr = Rect $_; $vr.X -gt $lr.Right -and [Math]::Abs(($vr.Y + $vr.Height / 2) - $cy) -lt 8 })[0].Current.Name })[0]
                foreach ($tx in $texts) {
                    $n = $tx.Current.Name
                    if (($ssid -and $n -like "*$ssid*") -or $n -match '\+\d+$' -or $n -match '^(Nome DNS|SSID)') { if ($n -notmatch '^(SSID|Nome DNS)\s*:?$') { $blur.Add((Rect $tx)) } }
                }
            }
            foreach ($b in $blur) { Pixelate $shot ([Drawing.Rectangle]::new($b.X - $pr.X - 3, $b.Y - $pr.Y - 2, $b.Width + 6, $b.Height + 4)) }
            $shot.Save((Join-Path $final "$name.png"))
            $report.Add("    borrados: $($blur.Count)")
        }
        $shot.Dispose()
        Esc
        if (@(Windows | Where-Object { $before -notcontains $_.Current.NativeWindowHandle }).Count -gt 0) { Esc }
    } catch { $report.Add("${name}: ERRO $($_.Exception.Message)"); Esc }
}

if (-not $Explore) {
    # Barra em duas metades (lado esquerdo: IA, cotas, tarefa e chat; direito: métricas e atalhos).
    $half = [int]($full.Width / 2) + 40
    foreach ($part in @(@('bar-left', 0), @('bar-right', ($full.Width - $half)))) {
        $crop = $full.Clone([Drawing.Rectangle]::new($part[1], 0, $half, $full.Height), $full.PixelFormat)
        $crop.Save((Join-Path $final "$($part[0]).png")); $crop.Dispose()
    }
    $full.Save((Join-Path $final 'bar.png'))
}
$full.Dispose()
$report | Set-Content (Join-Path $PSScriptRoot 'capture-readme.txt') -Encoding UTF8
