# Draws the app icon: a green circle with a white speech bubble.
#   src\WeChatNotifier.App\Assets\AppIcon.ico            exe + tray (16-256 px, each size drawn on its own pixel grid)
#   src\WeChatNotifier.Notifications\Assets\HeaderIcon-<n>.png  notification header, one per display scaling
# Small sizes (up to 32 px) and the notification header get a plain bubble: three dots of 1-2 px only smear
# when Windows shrinks the image to the 16-20 px header. The dots appear from 40 px up.
# Run with Windows PowerShell (System.Drawing):  powershell -File scripts\make-icons.ps1
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$repo = Split-Path -Parent $PSScriptRoot

$green = [System.Drawing.Color]::FromArgb(255, 35, 158, 99)
$white = [System.Drawing.Color]::White

function New-RoundedRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function Draw-Icon([int]$size, [bool]$dots) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [single]$size
    $greenBrush = New-Object System.Drawing.SolidBrush $green
    $whiteBrush = New-Object System.Drawing.SolidBrush $white
    $g.FillEllipse($greenBrush, 0, 0, $s, $s)

    # Bubble edges on whole pixels at small sizes, so they stay sharp.
    $x0 = [math]::Round(0.22 * $s); $x1 = [math]::Round(0.78 * $s)
    $y0 = [math]::Round(0.25 * $s); $y1 = [math]::Round(0.64 * $s)
    $r = [single][math]::Max(1.5, 0.13 * $s)
    $bubble = New-RoundedRect $x0 $y0 ($x1 - $x0) ($y1 - $y0) $r
    $g.FillPath($whiteBrush, $bubble)
    $tail = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF ([single](0.30 * $s)), ([single]($y1 - 0.5))),
        (New-Object System.Drawing.PointF ([single](0.27 * $s)), ([single](0.80 * $s))),
        (New-Object System.Drawing.PointF ([single](0.50 * $s)), ([single]($y1 - 0.5))))
    $g.FillPolygon($whiteBrush, $tail)

    if ($dots) {
        $d = [single](0.085 * $s); $cy = [single](($y0 + $y1) / 2)
        foreach ($cx in 0.36, 0.50, 0.64) { $g.FillEllipse($greenBrush, [single]($cx * $s - $d / 2), [single]($cy - $d / 2), $d, $d) }
    }
    $g.Dispose(); $greenBrush.Dispose(); $whiteBrush.Dispose(); $bubble.Dispose()
    return $bmp
}

function Get-PngBytes([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return ,$ms.ToArray() # the comma keeps PowerShell from unrolling the array into single bytes
}

# Notification header icons: the header draws the app icon 16 effective px wide, so one file per display scaling
# (100–300 %); the notifier registers the one for the primary display, which Windows then draws 1:1. A single large
# file was shrunk by Windows with a simple filter and its circle came out jagged (64 -> 20 px at 125 %).
$headerSizes = 16, 20, 24, 28, 32, 36, 40, 48
foreach ($size in $headerSizes) {
    $png = Draw-Icon $size $false
    [System.IO.File]::WriteAllBytes("$repo\src\WeChatNotifier.Notifications\Assets\HeaderIcon-$size.png", (Get-PngBytes $png))
    $png.Dispose()
}

# ICO with PNG-compressed frames (Windows Vista and later); 16-48 cover the tray at 100-300 % scaling.
$sizes = 16, 20, 24, 28, 32, 36, 40, 48, 64, 256
$frames = foreach ($size in $sizes) { $bmp = Draw-Icon $size ($size -ge 40); Get-PngBytes $bmp; $bmp.Dispose() }
$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ico
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$frames[$i].Length); $w.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($frame in $frames) { $w.Write($frame) }
$w.Flush()
[System.IO.File]::WriteAllBytes("$repo\src\WeChatNotifier.App\Assets\AppIcon.ico", $ico.ToArray())
Write-Host "HeaderIcon-*.png ($($headerSizes -join ', ') px) and AppIcon.ico ($($sizes -join ', ') px) written."
