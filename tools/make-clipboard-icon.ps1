# Gera o ícone do WinStatusBar (ico multi-tamanho + png) — executado apenas em tempo de desenvolvimento.
param([string]$OutDir = (Join-Path $PSScriptRoot '..\src\WinStatusBar\Assets'))
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

function New-IconBitmap([int]$size) {
    $source = [System.Drawing.Image]::FromFile((Join-Path $PSScriptRoot '..\src\WinStatusBar\Assets\Clipboard.png'))
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try { $g.Clear([System.Drawing.Color]::Transparent); $g.InterpolationMode = 'HighQualityBicubic'; $g.DrawImage($source, 0, 0, $size, $size) }
    finally { $g.Dispose(); $source.Dispose() }
    return $bmp
}

# Quadros < 256 px em DIB 32 bpp clássico (BGRA + máscara AND): a bandeja do Windows 11 nem sempre desenha
# quadros PNG pequenos. Só o de 256 px fica em PNG (formato padrão do Vista em diante).
function ConvertTo-IcoDib([System.Drawing.Bitmap]$b) {
    $s = $b.Width; $ms = New-Object IO.MemoryStream; $w = New-Object IO.BinaryWriter $ms
    $w.Write([uint32]40); $w.Write([int32]$s); $w.Write([int32]($s * 2)); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]0); $w.Write([uint32]0); $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)
    for ($y = $s - 1; $y -ge 0; $y--) { for ($x = 0; $x -lt $s; $x++) { $c = $b.GetPixel($x, $y); $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A) } }
    $stride = [int]([Math]::Ceiling($s / 32.0) * 4)
    for ($y = $s - 1; $y -ge 0; $y--) {
        $row = New-Object byte[] $stride
        for ($x = 0; $x -lt $s; $x++) { if ($b.GetPixel($x, $y).A -eq 0) { $row[[int][Math]::Floor($x / 8)] = $row[[int][Math]::Floor($x / 8)] -bor (0x80 -shr ($x % 8)) } }
        $w.Write($row)
    }
    $w.Flush(); return ,$ms.ToArray()
}
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$pngs = foreach ($s in $sizes) {
    $b = New-IconBitmap $s
    if ($s -ge 256) { $ms = New-Object IO.MemoryStream; $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $data = $ms.ToArray() } else { $data = ConvertTo-IcoDib $b }
    $b.Dispose(); ,$data
}
$ico = New-Object IO.MemoryStream; $bw = New-Object IO.BinaryWriter $ico
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = $pngs[$i]
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$d.Length); $bw.Write([uint32]$offset); $offset += $d.Length
}
foreach ($d in $pngs) { $bw.Write($d) }
[IO.File]::WriteAllBytes((Join-Path $OutDir 'Clipboard.ico'), $ico.ToArray())

"ok"
