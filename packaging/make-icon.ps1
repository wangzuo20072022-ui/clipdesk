# ClipDesk 图标生成器
# 用法: powershell -File packaging/make-icon.ps1
# 产出: src/ClipDesk/Assets/ClipDesk.ico  +  artifacts/icon-preview.png

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repo = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $repo 'src/ClipDesk/Assets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null
$icoPath = Join-Path $assets 'ClipDesk.ico'
$previewPath = Join-Path $repo 'artifacts/icon-preview.png'

# ── 配色（和面板/条子同一套深色调）────────────────────────────────
$bgTop     = [System.Drawing.Color]::FromArgb(255, 0x3A, 0x3A, 0x46)
$bgBottom  = [System.Drawing.Color]::FromArgb(255, 0x1A, 0x1A, 0x22)
$edgeLight = [System.Drawing.Color]::FromArgb(70, 0xFF, 0xFF, 0xFF)
$paper     = [System.Drawing.Color]::FromArgb(255, 0xF2, 0xF4, 0xF8)
$paperEdge = [System.Drawing.Color]::FromArgb(255, 0xC8, 0xCE, 0xDA)
$clipBody  = [System.Drawing.Color]::FromArgb(255, 0x8C, 0xD0, 0xFF)
$line1     = [System.Drawing.Color]::FromArgb(255, 0x6E, 0x74, 0x84)
$line2     = [System.Drawing.Color]::FromArgb(190, 0x6E, 0x74, 0x84)
$line3     = [System.Drawing.Color]::FromArgb(120, 0x6E, 0x74, 0x84)

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Draw-Icon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    $s = [float]$size
    $pad = $s * 0.055
    $radius = $s * 0.22

    # 底板：竖向渐变 + 圆角
    $bgPath = New-RoundedPath $pad $pad ($s - 2*$pad) ($s - 2*$pad) $radius
    $rect = New-Object System.Drawing.RectangleF(0, 0, $s, $s)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $bgTop, $bgBottom, 90.0)
    $g.FillPath($brush, $bgPath)

    # 顶边一道极细高光（玻璃感）
    $edgePen = New-Object System.Drawing.Pen($edgeLight, [float]([Math]::Max(0.6, $s * 0.012)))
    $g.DrawPath($edgePen, $bgPath)

    # ── 剪贴板纸 ───────────────────────────────────────────────
    $pw = $s * 0.46
    $ph = $s * 0.56
    $px = ($s - $pw) / 2
    $py = $s * 0.28

    # 纸后的深色投影，让它从底板上"浮"起来
    $shadowPath = New-RoundedPath ($px + $s*0.018) ($py + $s*0.022) $pw $ph ($s * 0.055)
    $shadowBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(110, 0, 0, 0))
    $g.FillPath($shadowBrush, $shadowPath)

    $paperPath = New-RoundedPath $px $py $pw $ph ($s * 0.055)
    $paperBrush = New-Object System.Drawing.SolidBrush($paper)
    $g.FillPath($paperBrush, $paperPath)
    $paperPen = New-Object System.Drawing.Pen($paperEdge, [float]([Math]::Max(0.5, $s * 0.008)))
    $g.DrawPath($paperPen, $paperPath)

    # ── 顶部夹子 ───────────────────────────────────────────────
    $cw = $pw * 0.46
    $ch = $s * 0.115
    $cx = $px + ($pw - $cw) / 2
    $cy = $py - $ch * 0.45
    $clipPath = New-RoundedPath $cx $cy $cw $ch ($ch * 0.42)
    $clipBrush = New-Object System.Drawing.SolidBrush($clipBody)
    $g.FillPath($clipBrush, $clipPath)

    # ── 纸上的三行字 ───────────────────────────────────────────
    $lw = $pw * 0.62
    $lx = $px + ($pw - $lw) / 2
    $lh = [float]([Math]::Max(1.0, $s * 0.036))
    $gap = $ph * 0.20
    $ly = $py + $ph * 0.30

    $pen1 = New-Object System.Drawing.Pen($line1, $lh); $pen1.StartCap = $pen1.EndCap = 'Round'
    $pen2 = New-Object System.Drawing.Pen($line2, $lh); $pen2.StartCap = $pen2.EndCap = 'Round'
    $pen3 = New-Object System.Drawing.Pen($line3, $lh); $pen3.StartCap = $pen3.EndCap = 'Round'

    $g.DrawLine($pen1, $lx, $ly, ($lx + $lw), $ly)
    $g.DrawLine($pen2, $lx, ($ly + $gap), ($lx + $lw), ($ly + $gap))
    $g.DrawLine($pen3, $lx, ($ly + 2*$gap), ($lx + $lw * 0.62), ($ly + 2*$gap))

    $g.Dispose()
    return $bmp
}

# ── 渲染所有尺寸 ───────────────────────────────────────────────────
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$bitmaps = @{}
foreach ($sz in $sizes) { $bitmaps[$sz] = Draw-Icon $sz }

# ── 打包成 .ico（PNG 压缩条目，Vista+ 支持）───────────────────────
$pngBytes = @{}
foreach ($sz in $sizes) {
    $ms = New-Object System.IO.MemoryStream
    $bitmaps[$sz].Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngBytes[$sz] = $ms.ToArray()
    $ms.Dispose()
}

$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)

# ICONDIR
$bw.Write([UInt16]0)                 # reserved
$bw.Write([UInt16]1)                 # type = icon
$bw.Write([UInt16]$sizes.Count)

# ICONDIRENTRY 表，先占位算出偏移
$offset = 6 + 16 * $sizes.Count
$entries = @()
foreach ($sz in $sizes) {
    $data = $pngBytes[$sz]
    $entries += [pscustomobject]@{ Size = $sz; Data = $data; Offset = $offset }
    $offset += $data.Length
}

foreach ($e in $entries) {
    $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
    $bw.Write([Byte]$dim)            # width
    $bw.Write([Byte]$dim)            # height
    $bw.Write([Byte]0)               # colorCount
    $bw.Write([Byte]0)               # reserved
    $bw.Write([UInt16]1)             # planes
    $bw.Write([UInt16]32)            # bitCount
    $bw.Write([UInt32]$e.Data.Length)
    $bw.Write([UInt32]$e.Offset)
}

foreach ($e in $entries) { $bw.Write($e.Data) }

$bw.Flush(); $bw.Dispose(); $fs.Dispose()

# ── 预览图：把 256 原图 + 各小尺寸并排拼一张 ──────────────────────
$prevW = 256 + 16 * 2 + (16+24+32+48+64+128) + 20 * 8
$prevH = 300
$prev = New-Object System.Drawing.Bitmap($prevW, $prevH, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$pg = [System.Drawing.Graphics]::FromImage($prev)
$pg.Clear([System.Drawing.Color]::FromArgb(255, 0x2A, 0x2A, 0x32))

# 大图
$pg.DrawImage($bitmaps[256], 20, 22, 256, 256)
# 小图横排（按实际像素画，不放大）
$x = 300
foreach ($sz in @(128, 64, 48, 32, 24, 16)) {
    $pg.DrawImage($bitmaps[$sz], $x, 22 + (256 - $sz), $sz, $sz)
    $x += $sz + 20
}
$pg.Dispose()
$prev.Save($previewPath, [System.Drawing.Imaging.ImageFormat]::Png)
$prev.Dispose()

foreach ($sz in $sizes) { $bitmaps[$sz].Dispose() }

Write-Host "ICO   -> $icoPath"
Write-Host "Preview -> $previewPath"
Get-Item $icoPath | ForEach-Object { Write-Host ("ICO size: {0} bytes" -f $_.Length) }
