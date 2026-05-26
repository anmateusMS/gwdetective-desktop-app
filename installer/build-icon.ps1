# Generates app.ico for GW Detective using the same WPF drawing primitives
# as MainWindow.xaml.cs / BuildAppIcon(). Run from the project root:
#   powershell -ExecutionPolicy Bypass -File installer\build-icon.ps1
# Produces:  app.ico   (multi-resolution: 16/20/24/32/48/64/128/256)
#
# Re-run this whenever the SVG/source icon in renderer or MainWindow changes
# so the in-EXE icon stays in sync with the in-app icon.

Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName WindowsBase

$ErrorActionPreference = 'Stop'

$sizes = 256,128,64,48,32,24,20,16
$accent = [System.Windows.Media.ColorConverter]::ConvertFromString('#facc15')

function New-IconPng([int]$pixelSize) {
    $scale = $pixelSize / 80.0
    $softFill = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromArgb(0x2E, $accent.R, $accent.G, $accent.B))
    $softFill.Freeze()

    function Stroke([double]$w, [double]$opacity = 1.0) {
        $b = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromArgb([byte]($opacity * 255), $accent.R, $accent.G, $accent.B))
        $b.Freeze()
        $sw = [Math]::Max(0.75, $w * $scale)
        $p = New-Object System.Windows.Media.Pen $b, $sw
        $p.StartLineCap = [System.Windows.Media.PenLineCap]::Round
        $p.EndLineCap   = [System.Windows.Media.PenLineCap]::Round
        $p.LineJoin     = [System.Windows.Media.PenLineJoin]::Round
        $p.Freeze()
        return $p
    }
    function P([double]$x, [double]$y) { New-Object System.Windows.Point ($x * $scale), ($y * $scale) }

    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    try {
        # Lens
        $dc.DrawEllipse($softFill, (Stroke 3),    (P 32 32), (22 * $scale), (22 * $scale))
        if ($pixelSize -ge 32) {
            $dc.DrawRoundedRectangle($null, (Stroke 2.2), (New-Object System.Windows.Rect ((P 22 20), (P 42 34))), (1.5 * $scale), (1.5 * $scale))
            if ($pixelSize -ge 48) {
                $dc.DrawLine((Stroke 1.6 0.75), (P 25 24), (P 36 24))
                $dc.DrawLine((Stroke 1.6 0.75), (P 25 27), (P 39 27))
                $dc.DrawLine((Stroke 1.6 0.75), (P 25 30), (P 33 30))
            }
            $dc.DrawLine((Stroke 2.4), (P 32 34), (P 32 40))
            $dc.DrawLine((Stroke 2.6), (P 27 41), (P 37 41))
        }
        if ($pixelSize -ge 48) {
            $shine = New-Object System.Windows.Media.StreamGeometry
            $sgc = $shine.Open()
            try {
                $sgc.BeginFigure((P 18 22), $false, $false)
                $sgc.QuadraticBezierTo((P 14 28), (P 16 34), $true, $false)
            } finally { $sgc.Close() }
            $shine.Freeze()
            $dc.DrawGeometry($null, (Stroke 2.5 0.45), $shine)
        }
        # Handle
        $dc.DrawLine((Stroke 5), (P 48 48), (P 68 68))
    } finally { $dc.Close() }

    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $pixelSize, $pixelSize, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($visual)
    $bmp.Freeze()

    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object System.IO.MemoryStream
    $enc.Save($ms)
    return ,$ms.ToArray()
}

$frames = @()
foreach ($s in $sizes) { $frames += ,(New-IconPng $s) }

# ICONDIR (6) + N * ICONDIRENTRY (16) + payloads
$out = New-Object System.IO.MemoryStream
$bw  = New-Object System.IO.BinaryWriter $out
$bw.Write([uint16]0)              # reserved
$bw.Write([uint16]1)              # type = icon
$bw.Write([uint16]$frames.Count)  # image count

$dataOffset = 6 + 16 * $frames.Count
for ($i = 0; $i -lt $frames.Count; $i++) {
    $s   = $sizes[$i]
    $len = $frames[$i].Length
    $dim = [byte]$(if ($s -ge 256) { 0 } else { $s })
    $bw.Write($dim)               # width
    $bw.Write($dim)               # height
    $bw.Write([byte]0)            # palette
    $bw.Write([byte]0)            # reserved
    $bw.Write([uint16]1)          # planes
    $bw.Write([uint16]32)         # bpp
    $bw.Write([uint32]$len)       # size
    $bw.Write([uint32]$dataOffset)# offset
    $dataOffset += $len
}
foreach ($f in $frames) { $bw.Write($f) }
$bw.Flush()

$projectRoot = Split-Path -Parent $PSScriptRoot
$icoPath = Join-Path $projectRoot 'app.ico'
[System.IO.File]::WriteAllBytes($icoPath, $out.ToArray())
Write-Host "Wrote $icoPath ($($out.Length) bytes, $($frames.Count) frames)"
