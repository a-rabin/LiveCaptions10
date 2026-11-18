# Regenerates Assets\app.ico (the "CC" badge) as a multi-size, PNG-compressed icon.
# Usage: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1 (run from the repo root)
param([string]$Out = (Join-Path $PSScriptRoot "..\src\LiveCaptions10\Assets\app.ico"))

Add-Type -AssemblyName System.Drawing

function New-BadgePng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([System.Drawing.Color]::Transparent)

    $inset = [Math]::Max(0.5, $size * 0.03)
    $x = $inset; $y = $size * 0.14; $w = $size - 2 * $inset; $h = $size * 0.72; $d = $size * 0.32
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $fill = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0x00, 0x67, 0xC0))
    $g.FillPath($fill, $path)

    $font = New-Object System.Drawing.Font "Segoe UI", ([float]($size * 0.46)), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
    $rect = New-Object System.Drawing.RectangleF (($x - $size), ($y + $size * 0.02), ($w + 2 * $size), $h)
    $g.DrawString("CC", $font, [System.Drawing.Brushes]::White, $rect, $fmt)

    $g.Dispose(); $font.Dispose(); $fill.Dispose(); $path.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($s in $sizes) { ,(New-BadgePng $s) }

$outDir = Split-Path $Out
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory $outDir | Out-Null }
$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$images[$i].Length); $bw.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Close()
Write-Host "Wrote $Out ($((Get-Item $Out).Length) bytes)"
