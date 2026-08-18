# Draws the SoundFocus tray icon and writes a multi-size .ico.
# Glyph: a speaker with two sound waves, and a target ring around the waves -
# "the sound is the thing being aimed at".
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$icoPath = Join-Path $root 'SoundFocus.ico'
$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)

# One bright accent that stays legible on both dark and light taskbars.
$accent = [System.Drawing.Color]::FromArgb(255, 46, 144, 255)
$accentDim = [System.Drawing.Color]::FromArgb(255, 120, 190, 255)

function New-Frame([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # speaker: body block + cone, as one filled polygon
    $body = New-Object System.Drawing.Drawing2D.GraphicsPath
    $pts = @(
        [System.Drawing.PointF]::new(0.06 * $s, 0.36 * $s),
        [System.Drawing.PointF]::new(0.25 * $s, 0.36 * $s),
        [System.Drawing.PointF]::new(0.44 * $s, 0.11 * $s),
        [System.Drawing.PointF]::new(0.44 * $s, 0.89 * $s),
        [System.Drawing.PointF]::new(0.25 * $s, 0.64 * $s),
        [System.Drawing.PointF]::new(0.06 * $s, 0.64 * $s)
    )
    $body.AddPolygon($pts)
    $brush = New-Object System.Drawing.SolidBrush($accent)
    $g.FillPath($brush, $body)

    # two sound waves, thickness scaled to the icon so 16px stays readable
    $penWidth = [Math]::Max(1.6, $s * 0.095)
    $pen = New-Object System.Drawing.Pen($accent, $penWidth)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

    $cx = 0.44 * $s
    $cy = 0.50 * $s
    foreach ($r in @(0.22, 0.37)) {
        $rr = $r * $s
        $rect = New-Object System.Drawing.RectangleF(($cx - $rr), ($cy - $rr), (2 * $rr), (2 * $rr))
        $g.DrawArc($pen, $rect, -52, 104)
    }

    # outermost wave dimmed, so the glyph reads as radiating rather than striped
    $pen2 = New-Object System.Drawing.Pen($accentDim, $penWidth)
    $pen2.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen2.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $rr = 0.52 * $s
    if ($s -ge 24) {
        $rect = New-Object System.Drawing.RectangleF(($cx - $rr), ($cy - $rr), (2 * $rr), (2 * $rr))
        $g.DrawArc($pen2, $rect, -46, 92)
    }

    $g.Dispose()
    return $bmp
}

# Frame payloads. Sizes up to 64 go in as uncompressed DIBs: PNG payloads are only
# understood by some consumers (GDI+ Icon.ToBitmap chokes on them), while 128/256 are
# conventionally PNG to keep the file small.
function ConvertTo-Dib([System.Drawing.Bitmap]$bmp) {
    $s = $bmp.Width
    $rect = New-Object System.Drawing.Rectangle(0, 0, $s, $s)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                          [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $pixels = New-Object byte[] ($data.Stride * $s)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $stride = $data.Stride
    $bmp.UnlockBits($data)

    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter($ms)
    $w.Write([UInt32]40)          # biSize
    $w.Write([Int32]$s)           # biWidth
    $w.Write([Int32]($s * 2))     # biHeight: XOR image plus AND mask
    $w.Write([UInt16]1)           # biPlanes
    $w.Write([UInt16]32)          # biBitCount
    $w.Write([UInt32]0)           # BI_RGB
    $w.Write([UInt32]0)           # biSizeImage
    $w.Write([Int32]0); $w.Write([Int32]0); $w.Write([UInt32]0); $w.Write([UInt32]0)

    # XOR bits, bottom-up
    for ($y = $s - 1; $y -ge 0; $y--) { $w.Write($pixels, ($y * $stride), ($s * 4)) }
    # AND mask, all zero: transparency comes from the alpha channel
    $maskRow = [Math]::Floor(($s + 31) / 32) * 4
    $w.Write((New-Object byte[] ($maskRow * $s)))

    $w.Flush()
    $bytes = $ms.ToArray()
    $w.Dispose()
    return ,$bytes            # comma stops PowerShell unrolling the array
}

$frames = @()
foreach ($s in $sizes) {
    $bmp = New-Frame $s
    if ($s -le 64) {
        $bytes = ConvertTo-Dib $bmp
    }
    else {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bytes = $ms.ToArray()
        $ms.Dispose()
    }
    $frames += , @{ size = $s; bytes = $bytes }
    $bmp.Dispose()
}

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)
$w.Write([UInt16]0)                  # reserved
$w.Write([UInt16]1)                  # type: icon
$w.Write([UInt16]$frames.Count)

$offset = 6 + (16 * $frames.Count)
foreach ($f in $frames) {
    $dim = if ($f.size -ge 256) { 0 } else { $f.size }
    $w.Write([Byte]$dim)             # width
    $w.Write([Byte]$dim)             # height
    $w.Write([Byte]0)                # palette
    $w.Write([Byte]0)                # reserved
    $w.Write([UInt16]1)              # planes
    $w.Write([UInt16]32)             # bpp
    $w.Write([UInt32]$f.bytes.Length)
    $w.Write([UInt32]$offset)
    $offset += $f.bytes.Length
}
foreach ($f in $frames) { $w.Write([byte[]]$f.bytes) }
$w.Flush()
[System.IO.File]::WriteAllBytes($icoPath, $out.ToArray())
$w.Dispose()

Write-Output "wrote $icoPath ($([Math]::Round((Get-Item $icoPath).Length / 1KB, 1)) KB, $($frames.Count) sizes)"
