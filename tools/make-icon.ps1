# Generates src/OB2.App/Resources/ob2.ico and the Chrome extension icons (extension/icons/icon-N.png):
# a blue rounded square with a white download arrow.
# Original artwork drawn in code; no third-party icons or assets. Run from the repo root: powershell -File tools/make-icon.ps1
Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$extensionSizes = 16, 32, 48, 128
$extensionDir = Join-Path (Get-Location) 'extension/icons'
New-Item -ItemType Directory -Force $extensionDir | Out-Null
$pngs = @()

foreach ($size in ($sizes + $extensionSizes | Sort-Object -Unique)) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    $r = [Math]::Max(2, [int]($size * 0.22))
    $rect = New-Object System.Drawing.RectangleF 0, 0, $size, $size
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($size - $d - 0.5, 0, $d, $d, 270, 90)
    $path.AddArc($size - $d - 0.5, $size - $d - 0.5, $d, $d, 0, 90)
    $path.AddArc(0, $size - $d - 0.5, $d, $d, 90, 90)
    $path.CloseFigure()
    $top = [System.Drawing.Color]::FromArgb(255, 37, 99, 235)
    $bottom = [System.Drawing.Color]::FromArgb(255, 29, 78, 216)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, $top, $bottom, 90
    $g.FillPath($brush, $path)

    # Arrow: shaft + head, then a tray line underneath.
    $white = [System.Drawing.Brushes]::White
    $cx = $size / 2.0
    $shaftW = $size * 0.16
    $g.FillRectangle($white, [float]($cx - $shaftW / 2), [float]($size * 0.18), [float]$shaftW, [float]($size * 0.34))
    $head = @(
        (New-Object System.Drawing.PointF ([float]($cx - $size * 0.27)), ([float]($size * 0.46))),
        (New-Object System.Drawing.PointF ([float]($cx + $size * 0.27)), ([float]($size * 0.46))),
        (New-Object System.Drawing.PointF ([float]$cx), ([float]($size * 0.74)))
    )
    $g.FillPolygon($white, $head)
    $g.FillRectangle($white, [float]($size * 0.22), [float]($size * 0.79), [float]($size * 0.56), [float]([Math]::Max(1, $size * 0.08)))

    if ($extensionSizes -contains $size) {
        $bmp.Save((Join-Path $extensionDir "icon-$size.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    if ($sizes -notcontains $size) {
        $g.Dispose(); $bmp.Dispose()
        continue
    }

    $ms = New-Object System.IO.MemoryStream
    if ($size -ge 256) {
        # Large entry as PNG (Vista+ format).
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    else {
        # Small entries as classic 32-bit DIBs, readable by every Win32 and System.Drawing API.
        $bw = New-Object System.IO.BinaryWriter $ms
        $bw.Write([uint32]40); $bw.Write([int32]$size); $bw.Write([int32]($size * 2))
        $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]0)
        $bw.Write([uint32]0); $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
        for ($y = $size - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $size; $x++) {
                $c = $bmp.GetPixel($x, $y)
                $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
            }
        }
        $maskRow = [int]([Math]::Ceiling($size / 32.0) * 4)
        $bw.Write((New-Object byte[] ($maskRow * $size)))
        $bw.Flush()
    }
    $pngs += , @($size, $ms.ToArray())
    $g.Dispose(); $bmp.Dispose()
}

$out = Join-Path (Get-Location) 'src/OB2.App/Resources/ob2.ico'
$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $s = $p[0]; $data = $p[1]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$data.Length); $w.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($p in $pngs) { $w.Write([byte[]]$p[1]) }
$w.Close()
Write-Output "Wrote $out"
