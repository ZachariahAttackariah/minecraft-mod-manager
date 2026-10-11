# Draws the app icon (the grass block from the instance picker) at several
# sizes and writes app.ico. Small sizes are 32-bit BMP entries, 256 is PNG.
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot "app.ico"

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconBitmap([int]$s) {
    # The same grass block as the instance picker (.block in the page): grass top, a darker edge, dirt
    # below, a light line along the top and a shadow along the bottom, with slightly rounded corners.
    $pad = if ($s -le 24) { 1 } else { [Math]::Round($s * 0.04) }
    $size = $s - 2 * $pad
    $grass = [Math]::Round($size * 0.30)
    $edge = [Math]::Max(1, [Math]::Round($size * 0.08))
    $light = [Math]::Max(1, [Math]::Round($size * 0.055))
    $shade = [Math]::Max(1, [Math]::Round($size * 0.083))

    # The bands are painted square first, then cut out with an anti-aliased rounded shape.
    $tile = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $t = [System.Drawing.Graphics]::FromImage($tile)
    $t.Clear([System.Drawing.Color]::Transparent)
    $t.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml("#8b5d3b"))), $pad, $pad, $size, $size)
    $t.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml("#74b84b"))), $pad, $pad, $size, $grass)
    $t.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml("#4f8f34"))), $pad, $pad + $grass, $size, $edge)
    $t.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(46, 255, 255, 255))), $pad, $pad, $size, $light)
    $t.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(77, 0, 0, 0))), $pad, $pad + $size - $shade, $size, $shade)
    $t.Dispose()

    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $radius = [Math]::Max(1, $size * 0.09)
    $g.FillPath((New-Object System.Drawing.TextureBrush $tile), (New-RoundedRect $pad $pad $size $size $radius))
    $g.Dispose()
    $tile.Dispose()
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
