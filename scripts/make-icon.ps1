# Draws the fan icon and writes a multi-size .ico (PNG frames) to src/FanPilot/Assets/fan.ico.
param([string]$Out = (Join-Path $PSScriptRoot '..\src\FanPilot\Assets\fan.ico'))

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-FanPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    $c = $size / 2.0
    $rect = New-Object System.Drawing.RectangleF 0, 0, $size, $size
    $top = [System.Drawing.Color]::FromArgb(255, 0x4C, 0xC2, 0xFF)
    $bottom = [System.Drawing.Color]::FromArgb(255, 0x00, 0x67, 0xC0)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, $top, $bottom, 45.0

    # Rounded-square plate.
    $r = $size * 0.22
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $r * 2, $r * 2, 180, 90)
    $path.AddArc($size - $r * 2 - 1, 0, $r * 2, $r * 2, 270, 90)
    $path.AddArc($size - $r * 2 - 1, $size - $r * 2 - 1, $r * 2, $r * 2, 0, 90)
    $path.AddArc(0, $size - $r * 2 - 1, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()
    $g.FillPath($brush, $path)

    # Four swept blades around a hub.
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(245, 255, 255, 255))
    for ($i = 0; $i -lt 4; $i++) {
        $state = $g.Save()
        $g.TranslateTransform($c, $c)
        $g.RotateTransform(90 * $i + 20)
        $blade = New-Object System.Drawing.Drawing2D.GraphicsPath
        $len = $size * 0.36
        $wid = $size * 0.17
        $blade.AddBezier(0, 0, $wid, -$len * 0.25, $wid * 0.9, -$len * 0.85, 0, -$len)
        $blade.AddBezier(0, -$len, -$wid * 0.7, -$len * 0.75, -$wid * 0.5, -$len * 0.3, 0, 0)
        $g.FillPath($white, $blade)
        $g.Restore($state)
    }
    $hub = $size * 0.14
    $g.FillEllipse($brush, $c - $hub / 2 - $size * 0.02, $c - $hub / 2 - $size * 0.02, $hub + $size * 0.04, $hub + $size * 0.04)
    $g.FillEllipse($white, $c - $hub / 2, $c - $hub / 2, $hub, $hub)

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$frames = foreach ($s in $sizes) { , (New-FanPng $s) }

$fs = [System.IO.File]::Create((Resolve-Path -LiteralPath (Split-Path $Out) | Join-Path -ChildPath (Split-Path $Out -Leaf)))
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$frames[$i].Length); $w.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($f in $frames) { $w.Write($f) }
$w.Close()
Write-Host "Wrote $Out"
