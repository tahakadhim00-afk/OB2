# Generates src/OB2.App/Resources/ob2.ico and the Chrome/Edge extension icons (extension/icons/icon-N.png)
# from the OB2 logo artwork in assets/logo/outside_app_main_icon_logo.png.
# The artwork's outer white background is made transparent (the white bird inside the circle is kept),
# the empty margin is cropped, and each size is resampled from the full-resolution image.
# Run from the repo root: powershell -ExecutionPolicy Bypass -File tools/make-icon.ps1
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class LogoIcon
{
    // Clears near-white pixels connected to the image border, then turns the anti-aliased rim
    // (ink blended with white) into ink with partial alpha, so no white halo shows on dark backgrounds.
    public static Bitmap RemoveOuterWhite(Bitmap source, Color ink, int tolerance)
    {
        int w = source.Width, h = source.Height;
        int[] px = Read(source);
        bool[] bg = new bool[w * h];
        Queue<int> queue = new Queue<int>();
        int limit = 255 - tolerance;
        for (int x = 0; x < w; x++) { Visit(px, bg, queue, x, limit); Visit(px, bg, queue, (h - 1) * w + x, limit); }
        for (int y = 0; y < h; y++) { Visit(px, bg, queue, y * w, limit); Visit(px, bg, queue, y * w + w - 1, limit); }
        while (queue.Count > 0)
        {
            int i = queue.Dequeue(), x = i % w, y = i / w;
            if (x > 0) Visit(px, bg, queue, i - 1, limit);
            if (x < w - 1) Visit(px, bg, queue, i + 1, limit);
            if (y > 0) Visit(px, bg, queue, i - w, limit);
            if (y < h - 1) Visit(px, bg, queue, i + w, limit);
        }

        int[] result = (int[])px.Clone();
        for (int i = 0; i < px.Length; i++)
        {
            if (bg[i]) { result[i] = 0; continue; }
            int x = i % w, y = i / w;
            if (!TouchesBackground(bg, x, y, w, h)) continue;
            Color c = Color.FromArgb(px[i]);
            double coverage = Math.Max((255.0 - c.R) / (255 - ink.R), (255.0 - c.G) / (255 - ink.G));
            coverage = Math.Min(1.0, Math.Max(0.0, coverage));
            result[i] = Color.FromArgb((int)Math.Round(coverage * c.A), ink.R, ink.G, ink.B).ToArgb();
        }

        return Write(result, w, h);
    }

    // Crops to the visible pixels and centres them on a transparent square with the given margin.
    public static Bitmap SquareCrop(Bitmap source, double margin)
    {
        int[] px = Read(source);
        int w = source.Width, h = source.Height, minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (((px[y * w + x] >> 24) & 0xFF) > 8)
                {
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                }

        int cw = maxX - minX + 1, ch = maxY - minY + 1;
        int side = (int)Math.Ceiling(Math.Max(cw, ch) * (1 + 2 * margin));
        Bitmap square = new Bitmap(side, side, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(square))
        {
            g.Clear(Color.Transparent);
            g.DrawImage(source, new Rectangle((side - cw) / 2, (side - ch) / 2, cw, ch), new Rectangle(minX, minY, cw, ch), GraphicsUnit.Pixel);
        }
        return square;
    }

    public static Bitmap Resize(Bitmap source, int size)
    {
        Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        using (ImageAttributes attributes = new ImageAttributes())
        {
            g.Clear(Color.Transparent);
            g.CompositingMode = CompositingMode.SourceCopy;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;
            attributes.SetWrapMode(WrapMode.TileFlipXY); // no dark seam along the edges
            g.DrawImage(source, new Rectangle(0, 0, size, size), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
        }
        return bmp;
    }

    private static void Visit(int[] px, bool[] bg, Queue<int> queue, int i, int limit)
    {
        if (bg[i]) return;
        int p = px[i], a = (p >> 24) & 0xFF, r = (p >> 16) & 0xFF, g = (p >> 8) & 0xFF, b = p & 0xFF;
        if (a < 16 || (r >= limit && g >= limit && b >= limit)) { bg[i] = true; queue.Enqueue(i); }
    }

    private static bool TouchesBackground(bool[] bg, int x, int y, int w, int h)
    {
        for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if (nx >= 0 && ny >= 0 && nx < w && ny < h && bg[ny * w + nx]) return true;
            }
        return false;
    }

    private static int[] Read(Bitmap source)
    {
        Rectangle rect = new Rectangle(0, 0, source.Width, source.Height);
        BitmapData data = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int[] px = new int[source.Width * source.Height];
        for (int y = 0; y < source.Height; y++)
            Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), px, y * source.Width, source.Width);
        source.UnlockBits(data);
        return px;
    }

    private static Bitmap Write(int[] px, int w, int h)
    {
        Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        for (int y = 0; y < h; y++)
            Marshal.Copy(px, y * w, IntPtr.Add(data.Scan0, y * data.Stride), w);
        bmp.UnlockBits(data);
        return bmp;
    }
}
'@

$root = Get-Location
$logoPath = Join-Path $root 'assets/logo/outside_app_main_icon_logo.png'
if (-not (Test-Path $logoPath)) { throw "Logo not found: $logoPath" }

$ink = [System.Drawing.Color]::FromArgb(255, 35, 94, 230) # OB2 blue, #235EE6
$artwork = New-Object System.Drawing.Bitmap $logoPath
$clean = [LogoIcon]::RemoveOuterWhite($artwork, $ink, 6)
$master = [LogoIcon]::SquareCrop($clean, 0.02)
$artwork.Dispose(); $clean.Dispose()

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$extensionSizes = 16, 32, 48, 128
$extensionDir = Join-Path $root 'extension/icons'
New-Item -ItemType Directory -Force $extensionDir | Out-Null
$pngs = @()

foreach ($size in ($sizes + $extensionSizes | Sort-Object -Unique)) {
    $bmp = [LogoIcon]::Resize($master, $size)

    if ($extensionSizes -contains $size) {
        $bmp.Save((Join-Path $extensionDir "icon-$size.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    if ($sizes -notcontains $size) {
        $bmp.Dispose()
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
    $bmp.Dispose()
}
$master.Dispose()

$out = Join-Path $root 'src/OB2.App/Resources/ob2.ico'
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
Write-Output "Wrote $out and $extensionDir/icon-{16,32,48,128}.png"
