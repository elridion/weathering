# Builds package/icon.png (256 x 256, the Thunderstore/Hexium mod icon) from the game-icons.net
# "hills" icon by Delapouite (CC BY 3.0): amber hills on a dark round plate with an amber
# rim. Heraldry, Spearbreaker and Warden use the same plate in their own colours, so the mods read as a set.
#
# The game-icons.net PNG archive (white on transparent) is downloaded to assets/source/ when missing;
# it is gitignored and only this script uses it.
#
# Usage (from the repo root, Windows PowerShell): powershell -File tools/make_icon.ps1

param(
    [string]$Archive = 'assets/source/game-icons.net.png.zip',
    [string]$Icon = 'delapouite/hills',
    [string]$Out = 'package/icon.png'
)

$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot)

if (-not (Test-Path $Archive)) {
    New-Item -ItemType Directory -Force (Split-Path $Archive) | Out-Null
    Invoke-WebRequest 'https://game-icons.net/archives/png/zip/ffffff/transparent/game-icons.net.png.zip' -UseBasicParsing -OutFile $Archive
}

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System; using System.Drawing; using System.Drawing.Drawing2D; using System.Drawing.Imaging;
public static class IconPaint {
    // Draws the white glyph as amber: a vertical gradient through the glyph's alpha, over a dark
    // outline grown from the same alpha.
    // The glyph's visible bounding box is scaled to span 2 * clipR px and centred, then everything
    // outside a circle of radius clipR (just inside the plate's rim) is cut with an antialiased edge,
    // so the glyph's corners are rounded off by the circle.
    public static Bitmap Glyph(Bitmap src, int size, double clipR, int outline) {
        var dst = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        int x0 = src.Width, y0 = src.Height, x1 = -1, y1 = -1;
        for (int y = 0; y < src.Height; y++)
            for (int x = 0; x < src.Width; x++)
                if (src.GetPixel(x, y).A > 16) { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x + 1); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y + 1); }
        double cx = (x0 + x1) / 2.0, cy = (y0 + y1) / 2.0;
        double s = 2 * clipR / Math.Max(x1 - x0, y1 - y0);
        var mask = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(mask)) {
            g.Clear(Color.Transparent);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(src, new RectangleF((float)(size / 2.0 - cx * s), (float)(size / 2.0 - cy * s), (float)(src.Width * s), (float)(src.Height * s)));
        }
        var a = new byte[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++) {
                double d = Math.Sqrt((x + 0.5 - size / 2.0) * (x + 0.5 - size / 2.0) + (y + 0.5 - size / 2.0) * (y + 0.5 - size / 2.0));
                double cover = Math.Max(0, Math.Min(1, clipR - outline + 0.5 - d)); // art stops one outline width inside clipR
                a[y * size + x] = (byte)Math.Round(mask.GetPixel(x, y).A * cover);
            }
        mask.Dispose();

        Color top = Color.FromArgb(255, 206, 110), bottom = Color.FromArgb(214, 132, 40), edge = Color.FromArgb(22, 18, 14);
        for (int y = 0; y < size; y++) {
            double t = (double)y / (size - 1);
            for (int x = 0; x < size; x++) {
                int ring = 0;
                for (int dy = -outline; dy <= outline; dy++)
                    for (int dx = -outline; dx <= outline; dx++) {
                        if (dx * dx + dy * dy > outline * outline) continue;
                        int sx = x + dx, sy = y + dy;
                        if (sx < 0 || sy < 0 || sx >= size || sy >= size) continue;
                        ring = Math.Max(ring, a[sy * size + sx]);
                    }
                int ga = a[y * size + x];
                double k = ga / 255.0;
                int r = (int)Math.Round(edge.R * (1 - k) + (top.R * (1 - t) + bottom.R * t) * k);
                int gg = (int)Math.Round(edge.G * (1 - k) + (top.G * (1 - t) + bottom.G * t) * k);
                int b = (int)Math.Round(edge.B * (1 - k) + (top.B * (1 - t) + bottom.B * t) * k);
                double dd = Math.Sqrt((x + 0.5 - size / 2.0) * (x + 0.5 - size / 2.0) + (y + 0.5 - size / 2.0) * (y + 0.5 - size / 2.0));
                double edgeCover = Math.Max(0, Math.Min(1, clipR + 0.5 - dd));
                dst.SetPixel(x, y, Color.FromArgb((int)Math.Round(Math.Max(ring, ga) * edgeCover), r, gg, b));
            }
        }
        return dst;
    }

    // The mod icon: the glyph on a dark round plate with a warm centre and an amber rim.
    public static Bitmap ModIcon(Bitmap glyph, int size) {
        var dst = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(dst)) {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using (var path = new GraphicsPath()) {
                path.AddEllipse(6, 6, size - 12, size - 12);
                using (var plate = new PathGradientBrush(path) { CenterColor = Color.FromArgb(255, 70, 62, 56), SurroundColors = new[] { Color.FromArgb(255, 22, 24, 28) } })
                    g.FillPath(plate, path);
                using (var rim = new Pen(Color.FromArgb(255, 232, 168, 64), 7f))
                    g.DrawEllipse(rim, 9.5f, 9.5f, size - 19, size - 19);
            }
            g.DrawImage(glyph, new Rectangle(0, 0, size, size));
        }
        return dst;
    }
}
'@

$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path $Archive))
try {
    $entry = $zip.GetEntry("icons/ffffff/transparent/1x1/$Icon.png")
    if (-not $entry) { throw "Icon '$Icon' not in the archive" }
    $stream = $entry.Open()
    try { $src = New-Object System.Drawing.Bitmap $stream } finally { $stream.Dispose() }
} finally {
    $zip.Dispose()
}

# The rim's inner edge is 115 px from the centre; clip 5 px inside it.
$glyph = [IconPaint]::Glyph($src, 256, 110, 6)
$mod = [IconPaint]::ModIcon($glyph, 256)
$mod.Save((Join-Path (Get-Location) $Out), [System.Drawing.Imaging.ImageFormat]::Png)

$glyph.Dispose(); $mod.Dispose(); $src.Dispose()
Write-Host "Wrote $Out from $Icon"
