# Draws the app icon (a lodestone block with a tilted compass needle) at several
# sizes and writes lodestone.ico. Small sizes are 32-bit BMP entries, 256 is PNG.
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot "lodestone.ico"

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
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [Math]::Max(0.5, $s * 0.05)
    $size = $s - 2 * $pad
    $block = New-RoundedRect $pad $pad $size $size ($s * 0.2)
    $rect = New-Object System.Drawing.RectangleF $pad, $pad, $size, $size
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 82, 92, 87)), ([System.Drawing.Color]::FromArgb(255, 34, 39, 37)), 90
    $g.FillPath($fill, $block)
    if ($s -ge 32) {
        $inset = $s * 0.045
        $inner = New-RoundedRect ($pad + $inset) ($pad + $inset) ($size - 2 * $inset) ($size - 2 * $inset) ($s * 0.15)
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(60, 255, 255, 255)), ([Math]::Max(1, $s / 40))
        $g.DrawPath($pen, $inner)
    }
    if ($s -ge 48) {
        $ring = $s * 0.33
        $ringPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(70, 255, 255, 255)), ([Math]::Max(1, $s / 64))
        $g.DrawEllipse($ringPen, $s / 2 - $ring, $s / 2 - $ring, $ring * 2, $ring * 2)
    }

    $g.TranslateTransform($s / 2, $s / 2)
    $g.RotateTransform(35)
    $len = $s * 0.31
    $wid = $s * 0.115
    $north = [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF 0, (-$len)), (New-Object System.Drawing.PointF $wid, 0), (New-Object System.Drawing.PointF (-$wid), 0))
    $south = [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF 0, $len), (New-Object System.Drawing.PointF $wid, 0), (New-Object System.Drawing.PointF (-$wid), 0))
    $g.FillPolygon((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 226, 74, 62))), $north)
    $g.FillPolygon((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 239, 236, 228))), $south)
    if ($s -ge 24) {
        $dot = $s * 0.045
        $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 27, 31, 29))), -$dot, -$dot, $dot * 2, $dot * 2)
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
