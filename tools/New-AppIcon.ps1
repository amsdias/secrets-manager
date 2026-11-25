<#
.SYNOPSIS
    Draws the app icon (src/SecretsManager.App/Assets/app.ico): a white key glyph on a blue rounded square.
    Re-run after changing the design; the .ico is committed so builds don't depend on this script.
#>
[CmdletBinding()]
param(
    [string]$OutFile = (Join-Path $PSScriptRoot '..\src\SecretsManager.App\Assets\app.ico'),
    [string]$PreviewPng
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$glyph = [string][char]0xE8D7   # "Permissions" (a key) in Segoe Fluent Icons / Segoe MDL2 Assets
$fontName = @('Segoe Fluent Icons', 'Segoe MDL2 Assets') |
    Where-Object { (New-Object System.Drawing.Text.InstalledFontCollection).Families.Name -contains $_ } |
    Select-Object -First 1
if (-not $fontName) { throw 'Neither Segoe Fluent Icons nor Segoe MDL2 Assets is installed.' }

function New-Frame([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $inset = [Math]::Max(0, [int]($size * 0.03))
    $rect = New-Object System.Drawing.RectangleF $inset, $inset, ($size - 2 * $inset), ($size - 2 * $inset)
    $r = $rect.Width * 0.22
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($rect.X, $rect.Y, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($rect.Right - 2 * $r, $rect.Y, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($rect.Right - 2 * $r, $rect.Bottom - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 0x2B, 0x88, 0xD8)), ([System.Drawing.Color]::FromArgb(255, 0x0F, 0x54, 0x9E)), 90
    $g.FillPath($brush, $path)

    $font = New-Object System.Drawing.Font $fontName, ([float]($size * 0.58)), ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
    $g.DrawString($glyph, $font, [System.Drawing.Brushes]::White, $rect, $fmt)
    $g.Dispose()
    $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-Frame $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($PreviewPng -and $s -eq 256) { $bmp.Save($PreviewPng, [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    ,$ms.ToArray()
}

# ICO container: header, one directory entry per size, then the PNG images.
New-Item -ItemType Directory -Force -Path (Split-Path $OutFile) | Out-Null
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Resolve-Path -LiteralPath (Split-Path $OutFile)).Path + '\' + (Split-Path $OutFile -Leaf), $out.ToArray())
"Wrote $OutFile ($($out.Length) bytes, font $fontName)"
