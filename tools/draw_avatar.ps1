# draw_avatar.ps1 -- generates the character art shipped in Assets\Default.
#
#   Assets\Default\default.png               neutral standing sprite
#   Assets\Default\Expressions\<anim>.png    one file per animation token
#
# The app maps the LLM's animation token (e.g. [anim:wave]) to
# <live2d.expression_directory>\<token>.png, so file names must match the
# tokens the model actually emits.
#
# Why a generator script instead of only committing PNGs: the art is
# placeholder-quality, and having it reproducible means anyone can tweak colors
# or proportions and re-render every expression consistently.
#
# ASCII only on purpose: Windows PowerShell 5.1 reads BOM-less .ps1 files as
# ANSI, so non-ASCII text inside the script would be mangled.
#
# Run with an execution-policy bypass, e.g.:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\draw_avatar.ps1

param(
    [string]$OutDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'Assets\Default')
)

Add-Type -AssemblyName System.Drawing

$Script:Canvas = 1024

function Col([string]$hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }

function New-SolidBrush([string]$hex) {
    New-Object System.Drawing.SolidBrush -ArgumentList (Col $hex)
}

function New-AlphaBrush([int]$r, [int]$g, [int]$b, [int]$a) {
    New-Object System.Drawing.SolidBrush -ArgumentList ([System.Drawing.Color]::FromArgb($a, $r, $g, $b))
}

function New-Pen([string]$hex, [double]$width) {
    $p = New-Object System.Drawing.Pen -ArgumentList (Col $hex), ([single]$width)
    $p.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $p.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $p.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    return $p
}

function New-Path { New-Object System.Drawing.Drawing2D.GraphicsPath }

# ------------------------------------------------------------------ palette
$C = @{
    HairDark   = '#38476A'
    HairMid    = '#50658C'
    HairShine  = '#8FA3C9'
    Skin       = '#FCE3D3'
    SkinShade  = '#EEC2AC'
    SkinLine   = '#C08A72'
    Dress      = '#FDFDFF'
    DressShade = '#D9E3F3'
    Ribbon     = '#E8607A'
    RibbonDark = '#C74661'
    Outline    = '#39404F'
    Sclera     = '#FFFFFF'
    IrisTop    = '#79C6F0'
    IrisBot    = '#2E6DA6'
    Pupil      = '#1B2434'
    Blush      = '#F2919A'
    MouthIn    = '#BB4F5C'
}

# ------------------------------------------------------------------ anchors
$HeadX  = 512.0
$HeadY  = 410.0
$HeadRx = 222.0
$HeadRy = 232.0
$EyeLx  = 430.0
$EyeRx  = 594.0
$EyeY   = 512.0
$BrowY  = 432.0
$MouthY = 600.0

function New-Canvas {
    $bmp = New-Object System.Drawing.Bitmap -ArgumentList $Script:Canvas, $Script:Canvas,
        ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode      = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode  = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode    = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    return @{ Bitmap = $bmp; Graphics = $g }
}

# ------------------------------------------------------------------ parts

function Draw-HairBack($g) {
    # mass behind the head: slightly narrower than the face so the chin shows
    $p = New-Path
    $p.AddEllipse(206.0, 150.0, 612.0, 632.0)
    $g.FillPath((New-SolidBrush $C.HairMid), $p)

    # twin tails hanging beside the body (thick enough to read as hair, not spikes)
    foreach ($side in @(-1, 1)) {
        $x0 = $HeadX + $side * 236.0
        $q = New-Path
        $q.AddBezier([single]$x0, [single]350.0,
                     [single]($x0 + $side * 72.0), [single]520.0,
                     [single]($x0 + $side * 62.0), [single]726.0,
                     [single]($x0 + $side * 30.0), [single]854.0)
        $q.AddBezier([single]($x0 + $side * 30.0), [single]854.0,
                     [single]($x0 - $side * 40.0), [single]726.0,
                     [single]($x0 - $side * 44.0), [single]520.0,
                     [single]($x0 - $side * 16.0), [single]350.0)
        $q.CloseFigure()
        $g.FillPath((New-SolidBrush $C.HairDark), $q)
    }
}

function Draw-Body($g, [bool]$wave) {
    # neck (short: the chin covers its top, the collar its bottom)
    $g.FillRectangle((New-SolidBrush $C.SkinShade), [single]482.0, [single]648.0, [single]60.0, [single]96.0)
    $g.FillRectangle((New-SolidBrush $C.Skin),      [single]490.0, [single]648.0, [single]44.0, [single]90.0)

    # dress: wide shoulders so it is not hidden behind the twin tails
    $d = New-Path
    $d.AddBezier([single]348.0, [single]1024.0,
                 [single]358.0, [single]852.0,
                 [single]400.0, [single]784.0,
                 [single]512.0, [single]776.0)
    $d.AddBezier([single]512.0, [single]776.0,
                 [single]624.0, [single]784.0,
                 [single]666.0, [single]852.0,
                 [single]676.0, [single]1024.0)
    $d.AddLine([single]676.0, [single]1024.0, [single]348.0, [single]1024.0)
    $d.CloseFigure()
    $g.FillPath((New-SolidBrush $C.Dress), $d)

    # left-side shading
    $sh = New-Path
    $sh.AddBezier([single]348.0, [single]1024.0,
                  [single]358.0, [single]852.0,
                  [single]400.0, [single]784.0,
                  [single]512.0, [single]776.0)
    $sh.AddLine([single]512.0, [single]776.0, [single]432.0, [single]1024.0)
    $sh.CloseFigure()
    $g.FillPath((New-AlphaBrush 176 194 228 90), $sh)

    # collar
    $g.FillEllipse((New-SolidBrush $C.DressShade), [single]424.0, [single]748.0, [single]176.0, [single]84.0)

    # neck ribbon
    $g.FillEllipse((New-SolidBrush $C.Ribbon), [single]444.0, [single]792.0, [single]66.0, [single]50.0)
    $g.FillEllipse((New-SolidBrush $C.Ribbon), [single]514.0, [single]792.0, [single]66.0, [single]50.0)
    $g.FillEllipse((New-SolidBrush $C.RibbonDark), [single]494.0, [single]798.0, [single]36.0, [single]38.0)

    if ($wave) {
        $s = New-Path
        $s.AddBezier([single]664.0, [single]900.0,
                     [single]730.0, [single]868.0,
                     [single]782.0, [single]818.0,
                     [single]812.0, [single]744.0)
        $s.AddBezier([single]812.0, [single]744.0,
                     [single]774.0, [single]724.0,
                     [single]734.0, [single]754.0,
                     [single]668.0, [single]852.0)
        $s.CloseFigure()
        $g.FillPath((New-SolidBrush $C.Dress), $s)

        $g.FillEllipse((New-SolidBrush $C.Skin), [single]758.0, [single]656.0, [single]102.0, [single]116.0)
        for ($i = 0; $i -lt 4; $i++) {
            $fx = 766.0 + $i * 24.0
            $fr = [math]::Abs(1.5 - $i) * 5.0
            $g.FillEllipse((New-SolidBrush $C.Skin), [single]$fx, [single](652.0 + $fr), [single]24.0, [single]46.0)
        }
        $g.DrawArc((New-Pen $C.SkinLine 3.0), [single]758.0, [single]656.0, [single]102.0, [single]116.0, 200.0, 120.0)
    }
}

function Draw-Head($g) {
    # ears first, so the face ellipse covers their inner half: drawn on top they
    # read as a step in the outline
    foreach ($side in @(-1, 1)) {
        $ex = $HeadX + $side * ($HeadRx - 4.0)
        $g.FillEllipse((New-SolidBrush $C.Skin), [single]($ex - 26.0), [single]($HeadY + 26.0), [single]52.0, [single]66.0)
    }

    $g.FillEllipse((New-SolidBrush $C.Skin),
        [single]($HeadX - $HeadRx), [single]($HeadY - $HeadRy),
        [single]($HeadRx * 2.0), [single]($HeadRy * 2.0 + 36.0))
}

function Draw-Bangs($g) {
    # cap across the top of the head, then a shallow zig-zag fringe that stops
    # above the brows (otherwise the eyebrows disappear into the hair)
    $p = New-Path
    $p.AddBezier([single]274.0, [single]430.0,
                 [single]278.0, [single]232.0,
                 [single]382.0, [single]176.0,
                 [single]512.0, [single]176.0)
    $p.AddBezier([single]512.0, [single]176.0,
                 [single]642.0, [single]176.0,
                 [single]746.0, [single]232.0,
                 [single]750.0, [single]430.0)

    $fringe = New-Object 'System.Drawing.PointF[]' 12
    $coords = @(
        @(714, 402), @(688, 448), @(646, 404),
        @(604, 446), @(558, 402), @(512, 444),
        @(466, 402), @(420, 446), @(378, 404),
        @(336, 448), @(310, 402), @(274, 430)
    )
    for ($i = 0; $i -lt $coords.Count; $i++) {
        $fringe[$i] = New-Object System.Drawing.PointF -ArgumentList ([single]$coords[$i][0]), ([single]$coords[$i][1])
    }
    $p.AddLines($fringe)
    $p.CloseFigure()
    $g.FillPath((New-SolidBrush $C.HairMid), $p)

    # shine band near the crown
    $sh = New-Path
    $sh.AddBezier([single]330.0, [single]268.0,
                  [single]404.0, [single]224.0,
                  [single]620.0, [single]224.0,
                  [single]694.0, [single]268.0)
    $sh.AddBezier([single]694.0, [single]268.0,
                  [single]620.0, [single]244.0,
                  [single]404.0, [single]244.0,
                  [single]330.0, [single]268.0)
    $sh.CloseFigure()
    $g.FillPath((New-AlphaBrush 148 166 206 140), $sh)

    # short strands in front of the ears
    foreach ($side in @(-1, 1)) {
        $x0 = $HeadX + $side * 226.0
        $q = New-Path
        $q.AddBezier([single]$x0, [single]318.0,
                     [single]($x0 + $side * 34.0), [single]430.0,
                     [single]($x0 + $side * 26.0), [single]540.0,
                     [single]($x0 + $side * 4.0), [single]612.0)
        $q.AddBezier([single]($x0 + $side * 4.0), [single]612.0,
                     [single]($x0 - $side * 14.0), [single]528.0,
                     [single]($x0 - $side * 20.0), [single]430.0,
                     [single]($x0 - $side * 12.0), [single]318.0)
        $q.CloseFigure()
        $g.FillPath((New-SolidBrush $C.HairDark), $q)
    }
}

function Draw-HeadRibbon($g) {
    $l = New-Path
    $l.AddBezier([single]706.0, [single]250.0,
                 [single]614.0, [single]182.0,
                 [single]594.0, [single]292.0,
                 [single]704.0, [single]276.0)
    $l.CloseFigure()
    $g.FillPath((New-SolidBrush $C.Ribbon), $l)

    $r = New-Path
    $r.AddBezier([single]732.0, [single]250.0,
                 [single]826.0, [single]182.0,
                 [single]846.0, [single]292.0,
                 [single]734.0, [single]276.0)
    $r.CloseFigure()
    $g.FillPath((New-SolidBrush $C.Ribbon), $r)

    $g.FillEllipse((New-SolidBrush $C.RibbonDark), [single]690.0, [single]228.0, [single]54.0, [single]54.0)
    $g.DrawArc((New-Pen '#F2A0B0' 5.0), [single]696.0, [single]234.0, [single]42.0, [single]42.0, 40.0, 130.0)
}

# ------------------------------------------------------------------ eyes

function Draw-EyeOpen($g, [double]$cx, [string]$variant) {
    $w = 116.0
    $h = 124.0
    if ($variant -eq 'shy')   { $h = 84.0 }
    if ($variant -eq 'angry') { $h = 96.0 }

    $top  = $EyeY - $h / 2.0
    $left = $cx - $w / 2.0

    $g.FillEllipse((New-SolidBrush $C.Sclera), [single]$left, [single]$top, [single]$w, [single]$h)

    $irisRect = New-Object System.Drawing.RectangleF -ArgumentList ([single]($cx - 40.0)), ([single]($EyeY - 40.0)), ([single]80.0), ([single]80.0)
    $irisBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush -ArgumentList $irisRect, (Col $C.IrisTop), (Col $C.IrisBot), ([System.Drawing.Drawing2D.LinearGradientMode]::Vertical)
    $g.FillEllipse($irisBrush, $irisRect)

    $g.FillEllipse((New-SolidBrush $C.Pupil), [single]($cx - 17.0), [single]($EyeY - 19.0), [single]34.0, [single]40.0)

    $g.FillEllipse((New-AlphaBrush 255 255 255 240), [single]($cx - 29.0), [single]($EyeY - 33.0), [single]26.0, [single]28.0)
    $g.FillEllipse((New-AlphaBrush 255 255 255 175), [single]($cx + 7.0),  [single]($EyeY + 12.0), [single]14.0, [single]14.0)

    # upper lash only -- an extra lower arc made the eye look like it sat in a cup
    $g.DrawArc((New-Pen $C.Outline 12.0), [single]($left - 4.0), [single]($top - 8.0), [single]($w + 8.0), [single]($h + 8.0), 198.0, 144.0)

    if ($variant -eq 'sad') {
        $t = New-Path
        $t.AddBezier([single]($cx + 28.0), [single]($EyeY + 30.0),
                     [single]($cx + 44.0), [single]($EyeY + 58.0),
                     [single]($cx + 38.0), [single]($EyeY + 76.0),
                     [single]($cx + 26.0), [single]($EyeY + 62.0))
        $t.AddBezier([single]($cx + 26.0), [single]($EyeY + 62.0),
                     [single]($cx + 16.0), [single]($EyeY + 52.0),
                     [single]($cx + 18.0), [single]($EyeY + 40.0),
                     [single]($cx + 28.0), [single]($EyeY + 30.0))
        $t.CloseFigure()
        $g.FillPath((New-AlphaBrush 150 210 245 210), $t)
    }
}

function Draw-EyeHappy($g, [double]$cx) {
    $g.DrawArc((New-Pen $C.Outline 13.0), [single]($cx - 54.0), [single]($EyeY - 22.0), [single]108.0, [single]84.0, 198.0, 144.0)
}

function Draw-Brows($g, [string]$variant) {
    foreach ($side in @(-1, 1)) {
        $cx = if ($side -lt 0) { $EyeLx } else { $EyeRx }
        $pen = New-Pen $C.HairDark 10.0
        $half = 40.0

        if ($variant -eq 'angry') {
            $x1 = $cx - $side * $half; $y1 = [single]($BrowY - 14.0)
            $x2 = $cx + $side * $half; $y2 = [single]($BrowY + 10.0)
        }
        elseif ($variant -eq 'sad') {
            $x1 = $cx - $side * $half; $y1 = [single]($BrowY + 10.0)
            $x2 = $cx + $side * $half; $y2 = [single]($BrowY - 14.0)
        }
        else {
            $x1 = $cx - $side * $half; $y1 = [single]$BrowY
            $x2 = $cx + $side * $half; $y2 = [single]$BrowY
        }

        $g.DrawLine($pen, [single]$x1, [single]$y1, [single]$x2, [single]$y2)
    }
}

# ------------------------------------------------------------------ mouth

function Draw-Mouth($g, [string]$variant) {
    switch ($variant) {
        'smile' {
            $g.DrawArc((New-Pen $C.MouthIn 7.0), [single]($HeadX - 40.0), [single]($MouthY - 22.0), [single]80.0, [single]48.0, 20.0, 140.0)
        }
        'grin' {
            $p = New-Path
            $p.AddBezier([single]($HeadX - 50.0), [single]($MouthY - 8.0),
                         [single]($HeadX - 28.0), [single]($MouthY + 44.0),
                         [single]($HeadX + 28.0), [single]($MouthY + 44.0),
                         [single]($HeadX + 50.0), [single]($MouthY - 8.0))
            $p.CloseFigure()
            $g.FillPath((New-SolidBrush $C.MouthIn), $p)
            $g.FillEllipse((New-AlphaBrush 240 130 140 190), [single]($HeadX - 24.0), [single]($MouthY + 12.0), [single]48.0, [single]20.0)
        }
        'open' {
            $g.FillEllipse((New-SolidBrush $C.MouthIn), [single]($HeadX - 24.0), [single]($MouthY - 10.0), [single]48.0, [single]40.0)
        }
        'frown' {
            $g.DrawArc((New-Pen $C.MouthIn 7.0), [single]($HeadX - 36.0), [single]($MouthY + 6.0), [single]72.0, [single]40.0, 200.0, 140.0)
        }
        default {
            $g.DrawLine((New-Pen $C.MouthIn 7.0), [single]($HeadX - 24.0), [single]($MouthY + 6.0), [single]($HeadX + 24.0), [single]($MouthY + 6.0))
        }
    }
}

# ------------------------------------------------------------------ compose

function Draw-Character {
    param(
        [string]$File,
        [string]$Eyes  = 'open',
        [string]$Mouth = 'smile',
        [string]$Brows = 'normal',
        [int]$Blush    = 95,
        [switch]$Wave
    )

    $canvas = New-Canvas
    $g = $canvas.Graphics

    Draw-HairBack $g
    Draw-Body $g ([bool]$Wave)
    Draw-Head $g

    if ($Blush -gt 0) {
        # cheeks: beside and slightly below the eyes, never below the mouth
        # (below the mouth it reads as a moustache)
        $bb = New-AlphaBrush 242 145 154 $Blush
        $g.FillEllipse($bb, [single]($EyeLx - 60.0), [single]($EyeY + 22.0), [single]120.0, [single]54.0)
        $g.FillEllipse($bb, [single]($EyeRx - 60.0), [single]($EyeY + 22.0), [single]120.0, [single]54.0)
    }

    Draw-Bangs $g
    Draw-HeadRibbon $g
    Draw-Brows $g $Brows

    if ($Eyes -eq 'happy') {
        Draw-EyeHappy $g $EyeLx
        Draw-EyeHappy $g $EyeRx
    }
    else {
        Draw-EyeOpen $g $EyeLx $Eyes
        Draw-EyeOpen $g $EyeRx $Eyes
    }

    Draw-Mouth $g $Mouth

    $g.Dispose()
    $canvas.Bitmap.Save($File, [System.Drawing.Imaging.ImageFormat]::Png)
    $canvas.Bitmap.Dispose()

    $len = (Get-Item $File).Length
    Write-Output ('  ' + (Split-Path $File -Leaf) + '  ' + [math]::Round($len / 1KB, 1) + ' KB')
}

# ------------------------------------------------------------------ main
$exprDir = Join-Path $OutDir 'Expressions'
New-Item -ItemType Directory -Path $OutDir  -Force | Out-Null
New-Item -ItemType Directory -Path $exprDir -Force | Out-Null

Write-Output 'generating character art:'

Draw-Character -File (Join-Path $OutDir 'default.png')   -Eyes open  -Mouth smile -Brows normal -Blush 95
Draw-Character -File (Join-Path $exprDir 'wave.png')     -Eyes open  -Mouth grin  -Brows normal -Blush 120 -Wave
Draw-Character -File (Join-Path $exprDir 'happy.png')    -Eyes happy -Mouth grin  -Brows normal -Blush 140
Draw-Character -File (Join-Path $exprDir 'smile.png')    -Eyes open  -Mouth smile -Brows normal -Blush 105
Draw-Character -File (Join-Path $exprDir 'nod.png')      -Eyes happy -Mouth smile -Brows normal -Blush 105
Draw-Character -File (Join-Path $exprDir 'sad.png')      -Eyes sad   -Mouth frown -Brows sad    -Blush 80
Draw-Character -File (Join-Path $exprDir 'angry.png')    -Eyes angry -Mouth frown -Brows angry  -Blush 110
Draw-Character -File (Join-Path $exprDir 'shy.png')      -Eyes shy   -Mouth smile -Brows sad    -Blush 180
Draw-Character -File (Join-Path $exprDir 'thinking.png') -Eyes open  -Mouth flat  -Brows sad    -Blush 60

Write-Output 'done.'
