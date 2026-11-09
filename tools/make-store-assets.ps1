# Gera os logotipos exigidos pelo pacote MSIX / Microsoft Store (mesmo desenho do ícone do app).
# Executado apenas em desenvolvimento; os PNGs ficam em src\WinStatusBar\Package\Assets.
param([string]$OutDir = (Join-Path $PSScriptRoot '..\src\WinStatusBar\Package\Assets'))
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

function New-IconBitmap([int]$size) {
    $source = [System.Drawing.Image]::FromFile((Join-Path $PSScriptRoot '..\src\WinStatusBar\Assets\WinStatusBar.png'))
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try { $g.Clear([System.Drawing.Color]::Transparent); $g.InterpolationMode = 'HighQualityBicubic'; $g.DrawImage($source, 0, 0, $size, $size) }
    finally { $g.Dispose(); $source.Dispose() }
    return $bmp
}

# Ícone centralizado numa tela transparente (largura x altura), ocupando 'fill' da menor dimensão.
function Save-Logo([string]$name, [int]$width, [int]$height, [double]$fill) {
    $icon = [int][Math]::Round([Math]::Min($width, $height) * $fill)
    $canvas = New-Object System.Drawing.Bitmap $width, $height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($canvas); $g.Clear([System.Drawing.Color]::Transparent)
    $src = New-IconBitmap $icon
    $g.DrawImage($src, [int](($width - $icon) / 2), [int](($height - $icon) / 2), $icon, $icon)
    $g.Dispose(); $src.Dispose()
    $canvas.Save((Join-Path $OutDir $name), [System.Drawing.Imaging.ImageFormat]::Png); $canvas.Dispose()
}

foreach ($scale in 100, 200) {
    $k = $scale / 100
    Save-Logo "Square44x44Logo.scale-$scale.png"   (44 * $k)  (44 * $k)  1.0
    Save-Logo "Square150x150Logo.scale-$scale.png" (150 * $k) (150 * $k) 0.66
    Save-Logo "Wide310x150Logo.scale-$scale.png"   (310 * $k) (150 * $k) 0.66
    Save-Logo "StoreLogo.scale-$scale.png"         (50 * $k)  (50 * $k)  1.0
}
# Ícones da barra de tarefas / menu Iniciar sem a placa colorida do sistema
foreach ($t in 16, 24, 32, 48, 256) {
    Save-Logo "Square44x44Logo.targetsize-$t.png" $t $t 1.0
    Save-Logo "Square44x44Logo.targetsize-${t}_altform-unplated.png" $t $t 1.0
}
Get-ChildItem $OutDir | Select-Object Name, Length
