# 把屏幕上指定的一块区域放大存成 PNG，用来量条子的真实像素尺寸。
#
# 用法（在 S2-Strip 目录下，用普通 PowerShell 跑，不需要管理员）：
#     powershell -File tools\measure.ps1
#
# 默认截条子那一块（工作区宽度 3/4 处、顶边），放大 8 倍存到 out\strip-zoom.png。
# 想量别的地方就改下面的 -X -Y -W -H 参数。

param(
    [int]$X = -1,          # -1 = 自动按"工作区宽度 3/4"算
    [int]$Y = 0,
    [int]$W = 160,         # 截取宽度（比条子 113px 宽一点，留出边界好判断）
    [int]$H = 60,          # 截取高度
    [int]$Zoom = 8,
    [string]$Out = "out\strip-zoom.png"
)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# 拿工作区（和程序用的是同一套 API）
$wa = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
if ($X -lt 0) { $X = [int]($wa.Left + $wa.Width * 0.75) - [int]($W / 2) }

$bmp = New-Object System.Drawing.Bitmap($W, $H)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($X, $Y, 0, 0, (New-Object System.Drawing.Size($W, $H)))
$g.Dispose()

# 放大：NearestNeighbor 才能看出一个个像素格，不要用平滑插值
$big = New-Object System.Drawing.Bitmap(($W * $Zoom), ($H * $Zoom))
$g2 = [System.Drawing.Graphics]::FromImage($big)
$g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$g2.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
$g2.DrawImage($bmp, 0, 0, ($W * $Zoom), ($H * $Zoom))
$g2.Dispose()

# 目录不在就建
$dir = Split-Path -Parent $Out
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }

$big.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose(); $big.Dispose()

Write-Host "截取区域 : ($X,$Y) ${W}x${H}"
Write-Host "放大倍数 : ${Zoom}x"
Write-Host "输出     : $Out"
Write-Host ""
Write-Host "打开这张图数格子：条子占了几行像素，那几行乘起来就是它的物理高度。"
Write-Host "目标：20mm 长 / 2mm 厚，150% 缩放下应该是 113 x 11 像素。"
