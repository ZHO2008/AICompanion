# ============================================================================
#  make_icon.ps1 - 生成一个"自己的"应用图标 Assets\app.ico
#
#  为什么需要它：
#    原先的 app.ico 是从用户提供的第三方角色立绘裁出来的，属于衍生作品，
#    不适合随公开仓库分发。这里用纯几何图形画一个中性图标，
#    既保持可构建，又不牵扯任何第三方素材的授权问题。
#
#  格式说明（这里踩过一次坑）：
#    ICO 支持内嵌 PNG，但**只有 256×256 用 PNG 是通用做法**；小尺寸必须是
#    BMP(DIB) 格式，否则 CSC 在生成 Win32 资源时报 CS7065。
#    本脚本只输出一帧 256×256 PNG，由系统缩放到 16/24/32/48，
#    简单、可自校验、不踩格式雷。
#
#  用法：
#    powershell -ExecutionPolicy Bypass -File tools\make_icon.ps1
#    powershell -ExecutionPolicy Bypass -File tools\make_icon.ps1 -Out D:\tmp\icon.ico
# ============================================================================

[CmdletBinding()]
param(
    [string]$Out = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($Out)) {
    $Out = Join-Path $root 'Assets\app.ico'
}

$dir = Split-Path -Parent $Out
if (-not (Test-Path $dir)) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
}

Add-Type -AssemblyName System.Drawing

$size = 256
$s = [double]$size

$bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.Clear([System.Drawing.Color]::Transparent)

# ---- 圆角方块底：紫 -> 蓝 斜向渐变 ----
$inset = $s * 0.05
$rect = New-Object System.Drawing.RectangleF($inset, $inset, ($s - 2 * $inset), ($s - 2 * $inset))
$radius = $s * 0.24
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$path.AddArc($rect.X, $rect.Y, ($radius * 2), ($radius * 2), 180, 90)
$path.AddArc(($rect.Right - $radius * 2), $rect.Y, ($radius * 2), ($radius * 2), 270, 90)
$path.AddArc(($rect.Right - $radius * 2), ($rect.Bottom - $radius * 2), ($radius * 2), ($radius * 2), 0, 90)
$path.AddArc($rect.X, ($rect.Bottom - $radius * 2), ($radius * 2), ($radius * 2), 90, 90)
$path.CloseFigure()

$brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    (New-Object System.Drawing.PointF(0, 0)),
    (New-Object System.Drawing.PointF($s, $s)),
    [System.Drawing.Color]::FromArgb(255, 108, 92, 231),
    [System.Drawing.Color]::FromArgb(255, 52, 152, 219))
$g.FillPath($brush, $path)

# ---- 白色对话气泡（圆 + 小尾巴）----
$white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 250, 250, 255))
$bubble = New-Object System.Drawing.RectangleF(($s * 0.20), ($s * 0.24), ($s * 0.60), ($s * 0.44))
$g.FillEllipse($white, $bubble)

$tailPts = @(
    (New-Object System.Drawing.PointF(($s * 0.36), ($s * 0.60))),
    (New-Object System.Drawing.PointF(($s * 0.31), ($s * 0.80))),
    (New-Object System.Drawing.PointF(($s * 0.55), ($s * 0.63)))
)
$g.FillPolygon($white, $tailPts)

# ---- 气泡里两只"眼睛"，一眼看出是伴侣而不是工具箱 ----
$accent = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 88, 74, 200))
$g.FillEllipse($accent, ($s * 0.34), ($s * 0.385), ($s * 0.10), ($s * 0.13))
$g.FillEllipse($accent, ($s * 0.56), ($s * 0.385), ($s * 0.10), ($s * 0.13))

$g.Dispose()
$brush.Dispose()
$white.Dispose()
$accent.Dispose()
$path.Dispose()

# ---- 存 PNG ----
$ms = New-Object System.IO.MemoryStream
$bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
$png = $ms.ToArray()
$ms.Dispose()
$bmp.Dispose()

# ---- 组装单帧 ICO：0..5 头 + 6..21 目录项 + 22.. PNG ----
$ico = New-Object System.Collections.Generic.List[byte]
$ico.AddRange([byte[]](0, 0, 1, 0, 1, 0))
$ico.AddRange([byte[]](0, 0, 0, 0))
$ico.AddRange([byte[]](1, 0))                            # 平面数 1
$ico.AddRange([byte[]](32, 0))
$ico.AddRange([System.BitConverter]::GetBytes([int]$png.Length))
$ico.AddRange([System.BitConverter]::GetBytes([int](6 + 16)))
$ico.AddRange($png)

$all = $ico.ToArray()
[System.IO.File]::WriteAllBytes($Out, $all)

# ---- 自检：结构能被解析回原样，PNG 魔数正确 ----
$check = [System.IO.File]::ReadAllBytes($Out)
$ok = ($check.Length -eq $all.Length) -and
      ($check[0] -eq 0) -and ($check[1] -eq 0) -and ($check[2] -eq 1) -and ($check[3] -eq 0) -and
      ($check[4] -eq 1) -and ($check[5] -eq 0) -and
      ($check[6] -eq 0) -and ($check[7] -eq 0) -and
      ($check[10] -eq 1) -and ($check[12] -eq 32) -and
      ($check[22] -eq 0x89) -and ($check[23] -eq 0x50) -and ($check[24] -eq 0x4E) -and ($check[25] -eq 0x47)

if (-not $ok) { throw 'ICO 自检失败：结构不符合预期' }

'  已生成: ' + $Out
'  规格: 256x256 / 32bpp / 内嵌 PNG，' + [math]::Round($all.Length/1KB, 1) + ' KB'
'  自检: 通过（目录项 16 字节、偏移 22、PNG 魔数 89 50 4E 47）'
'  说明: 纯几何图形（圆角渐变底 + 对话气泡 + 两只眼睛），不含任何第三方素材。'
