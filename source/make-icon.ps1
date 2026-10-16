# Draws the app icon, a grass block at Minecraft's in-game angle in 16x16 pixel art, at several sizes and
# writes app.ico. Small sizes are 32-bit BMP entries, 256 is PNG. Pixels are scaled by whole numbers so they
# stay square and sharp; a size that isn't a multiple of 16 gets the biggest whole scale that fits, centred,
# unless that would leave too wide a margin, in which case the pixels stretch to fill it.
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot "app.ico"

# One letter per colour; "." is transparent. Top face lit, left side a little darker, right side darker still.
$palette = @{
    A = "#86c75f"; B = "#6aab48"; C = "#7cbd56"; D = "#5f9f3f"; E = "#4e8234"; F = "#578c3b"; G = "#527d39"; H = "#3f692a"; I = "#467130"
    J = "#724c30"; K = "#64422b"; L = "#669b47"; M = "#513522"; N = "#7b5739"; O = "#63462e"; P = "#49311f"; Q = "#5a3d27"; R = "#5c3d27"
}
$art = @(
    "......ABAC......",
    "....DACCCCBC....",
    "..CCBCCDCCDCDA..",
    "BCCCCCDDCCCDCBCD",
    "EFDBCABAAABCDBGH",
    "EEFFAAABACDBIGHH",
    "JKELFFDCBDGHHHIM",
    "NJNFLFFEGHIHGHOP",
    "NQKJKNLLHGHMMOPP",
    "JJKJJNJKHPPRORPM",
    "KNJQKKNKOPMPROOR",
    "NQNJQKKQPRPMRRRP",
    "QNJKQKJJROOMORPR",
    "..QJKJNKORRRRM..",
    "....JQKJRMMO....",
    "......QNOR......"
)

function New-IconBitmap([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    $whole = [Math]::Max(1, [Math]::Floor($s / 16))
    $stretch = ($s - 16 * $whole) -gt $s * 0.2
    $offset = if ($stretch) { 0 } else { [Math]::Floor(($s - 16 * $whole) / 2) }
    for ($y = 0; $y -lt 16; $y++) {
        for ($x = 0; $x -lt 16; $x++) {
            $ch = [string]$art[$y][$x]
            if ($ch -eq ".") { continue }
            $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml($palette[$ch]))
            if ($stretch) {
                # Edges by rounding, so neighbouring pixels meet without gaps or overlaps.
                $x0 = [Math]::Round($x * $s / 16); $x1 = [Math]::Round(($x + 1) * $s / 16)
                $y0 = [Math]::Round($y * $s / 16); $y1 = [Math]::Round(($y + 1) * $s / 16)
                $g.FillRectangle($brush, $x0, $y0, $x1 - $x0, $y1 - $y0)
            } else {
                $g.FillRectangle($brush, $offset + $x * $whole, $offset + $y * $whole, $whole, $whole)
            }
            $brush.Dispose()
        }
    }
    $g.Dispose()
    return $bmp
}
# 32-bit DIB icon entry: BITMAPINFOHEADER, bottom-up BGRA pixels, empty AND mask.
function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $s = $bmp.Width
    $data = $bmp.LockBits((New-Object System.Drawing.Rectangle 0, 0, $s, $s), [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $pixels = New-Object byte[] ($stride * $s)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $bmp.UnlockBits($data)
    $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([int]40); $w.Write([int]$s); $w.Write([int]($s * 2)); $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]0); $w.Write([int]($s * $s * 4 + $maskRow * $s)); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
    for ($y = $s - 1; $y -ge 0; $y--) { $w.Write($pixels, $y * $stride, $s * 4) }
    $w.Write((New-Object byte[] ($maskRow * $s)))
    $w.Flush()
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    if ($s -eq 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        , [byte[]]$ms.ToArray()
    } else {
        , [byte[]](Get-DibBytes $bmp)
    }
    $bmp.Dispose()
}

$file = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $file
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $bytes = $images[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$bytes.Length); $w.Write([int]$offset)
    $offset += $bytes.Length
}
foreach ($bytes in $images) { $w.Write([byte[]]$bytes) }
$w.Flush()
[System.IO.File]::WriteAllBytes($out, $file.ToArray())
"Wrote $out ($($file.Length) bytes, sizes $($sizes -join ', '))"
