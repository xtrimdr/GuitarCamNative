# Draws the GuitarCam Native icon and writes a multi-size .ico (plus a 256 px PNG to preview it).
# Same idea as the web app's launcher icon (guitar pick + camera + rec dot), with more depth:
# a shaded classic-shape pick with a rim light and a video camera (body, lens, hood) with a rec light.
# Small sizes (<= 32 px) skip the fine detail so they stay readable.
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'src\GuitarCam.App\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null
$out = Join-Path $assets 'GuitarCam.ico'

function C([int]$a, [int]$r, [int]$g, [int]$b) { [System.Drawing.Color]::FromArgb($a, $r, $g, $b) }
function Circle([float]$x, [float]$y, [float]$r) {
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $p.AddEllipse($x - $r, $y - $r, 2 * $r, 2 * $r); $p
}
function Radial($path, [float]$cx, [float]$cy, $center, $edge) {
  $b = New-Object System.Drawing.Drawing2D.PathGradientBrush $path
  $b.CenterPoint = New-Object System.Drawing.PointF $cx, $cy
  $b.CenterColor = $center; $b.SurroundColors = @($edge); $b
}

function New-IconPng([int]$s) {
  $bmp = New-Object System.Drawing.Bitmap $s, $s
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
  $g.Clear([System.Drawing.Color]::Transparent)
  $k = $s / 256.0
  $small = $s -le 32
  $pt = { param($x, $y) New-Object System.Drawing.PointF ($x * $k), ($y * $k) }

  # Rounded-square background with a faint top rim
  $m = if ($small) { 0 } else { 4 * $k }
  $r = 54 * $k; $w = $s - 2 * $m
  $bg = New-Object System.Drawing.Drawing2D.GraphicsPath
  $bg.AddArc($m, $m, 2 * $r, 2 * $r, 180, 90); $bg.AddArc($m + $w - 2 * $r, $m, 2 * $r, 2 * $r, 270, 90)
  $bg.AddArc($m + $w - 2 * $r, $m + $w - 2 * $r, 2 * $r, 2 * $r, 0, 90); $bg.AddArc($m, $m + $w - 2 * $r, 2 * $r, 2 * $r, 90, 90)
  $bg.CloseFigure()
  $g.FillPath((New-Object System.Drawing.Drawing2D.LinearGradientBrush (& $pt 0 0), (& $pt 0 256), (C 255 36 36 41), (C 255 11 11 13)), $bg)
  if (-not $small) {
    $rim = New-Object System.Drawing.Drawing2D.LinearGradientBrush (& $pt 0 0), (& $pt 0 256), (C 70 255 255 255), (C 0 255 255 255)
    $g.DrawPath((New-Object System.Drawing.Pen $rim, ([Math]::Max(1.5 * $k, 1))), $bg)
  }

  # Guitar pick: classic 351 shape, wide shoulders tapering to a rounded tip
  $pick = New-Object System.Drawing.Drawing2D.GraphicsPath
  $pick.AddBezier((& $pt 128 30), (& $pt 176 30), (& $pt 214 34), (& $pt 228 58))
  $pick.AddBezier((& $pt 228 58), (& $pt 240 80), (& $pt 214 140), (& $pt 150 214))
  $pick.AddBezier((& $pt 150 214), (& $pt 138 228), (& $pt 118 228), (& $pt 106 214))
  $pick.AddBezier((& $pt 106 214), (& $pt 42 140), (& $pt 16 80), (& $pt 28 58))
  $pick.AddBezier((& $pt 28 58), (& $pt 42 34), (& $pt 80 30), (& $pt 128 30))
  $pick.CloseFigure()
  if (-not $small) {
    $sh = $pick.Clone(); $mx = New-Object System.Drawing.Drawing2D.Matrix; $mx.Translate(0, 6 * $k); $sh.Transform($mx)
    $g.FillPath((New-Object System.Drawing.SolidBrush (C 110 0 0 0)), $sh)
  }
  $pb = New-Object System.Drawing.Drawing2D.LinearGradientBrush (& $pt 60 30), (& $pt 190 228), (C 255 125 238 252), (C 255 8 132 166)
  $g.FillPath($pb, $pick)
  if (-not $small) {
    $rl = New-Object System.Drawing.Drawing2D.LinearGradientBrush (& $pt 0 30), (& $pt 0 228), (C 200 220 252 255), (C 0 220 252 255)
    $g.DrawPath((New-Object System.Drawing.Pen $rl, (3 * $k)), $pick)
    $g.DrawPath((New-Object System.Drawing.Pen (C 90 4 60 80), (1.2 * $k)), $pick)
  }

  # Video camera: a rounded body with a lens, and the wedge-shaped hood on the right
  $dark = New-Object System.Drawing.Drawing2D.LinearGradientBrush (& $pt 0 80), (& $pt 0 156), (C 255 34 34 39), (C 255 10 10 12)
  $bx = 50; $by = 82; $bw = 100; $bh = 68; $br = 14
  $body = New-Object System.Drawing.Drawing2D.GraphicsPath
  $body.AddArc(($bx * $k), ($by * $k), (2 * $br * $k), (2 * $br * $k), 180, 90)
  $body.AddArc((($bx + $bw - 2 * $br) * $k), ($by * $k), (2 * $br * $k), (2 * $br * $k), 270, 90)
  $body.AddArc((($bx + $bw - 2 * $br) * $k), (($by + $bh - 2 * $br) * $k), (2 * $br * $k), (2 * $br * $k), 0, 90)
  $body.AddArc(($bx * $k), (($by + $bh - 2 * $br) * $k), (2 * $br * $k), (2 * $br * $k), 90, 90)
  $body.CloseFigure()
  $hood = New-Object System.Drawing.Drawing2D.GraphicsPath
  $hood.AddPolygon([System.Drawing.PointF[]]@((& $pt 156 104), (& $pt 200 82), (& $pt 200 150), (& $pt 156 128)))
  if (-not $small) {
    foreach ($p in $body, $hood) {
      $sp = $p.Clone(); $mx = New-Object System.Drawing.Drawing2D.Matrix; $mx.Translate(0, 4 * $k); $sp.Transform($mx)
      $g.FillPath((New-Object System.Drawing.SolidBrush (C 80 0 30 45)), $sp)
    }
  }
  $g.FillPath($dark, $body)
  $hp = New-Object System.Drawing.Pen $dark, (8 * $k); $hp.LineJoin = 'Round'
  $g.DrawPath($hp, $hood); $g.FillPath($dark, $hood)

  $cx = 100 * $k; $cy = 116 * $k
  if ($small) {
    $g.FillPath((New-Object System.Drawing.SolidBrush (C 255 34 211 238)), (Circle $cx $cy (17 * $k)))
    $g.FillPath((New-Object System.Drawing.SolidBrush (C 255 10 10 12)), (Circle $cx $cy (9 * $k)))
  } else {
    $g.FillPath((New-Object System.Drawing.Drawing2D.LinearGradientBrush (& $pt 0 92), (& $pt 0 140), (C 255 80 80 88), (C 255 22 22 25)), (Circle $cx $cy (24 * $k)))
    $glass = Circle $cx $cy (19 * $k)
    $g.FillPath((Radial $glass ($cx + 4 * $k) ($cy + 5 * $k) (C 255 24 110 136) (C 255 8 14 20)), $glass)
    $g.FillPath((New-Object System.Drawing.SolidBrush (C 255 6 8 10)), (Circle $cx $cy (7 * $k)))
    $arc = New-Object System.Drawing.Pen (C 140 190 245 255), (3 * $k)
    $arc.StartCap = 'Round'; $arc.EndCap = 'Round'
    $g.DrawArc($arc, $cx - 14 * $k, $cy - 14 * $k, 28 * $k, 28 * $k, 200, 60)
    $g.FillPath((New-Object System.Drawing.SolidBrush (C 230 210 250 255)), (Circle ($cx - 8 * $k) ($cy - 8 * $k) (3.5 * $k)))
  }

  # Rec dot on the camera body
  $rr = [Math]::Max(8 * $k, 1.6); $rx = 136 * $k; $ry = 96 * $k
  $dot = Circle $rx $ry $rr
  if ($small) {
    $g.FillPath((New-Object System.Drawing.SolidBrush (C 255 239 68 68)), $dot)
  } else {
    $g.FillPath((New-Object System.Drawing.SolidBrush (C 90 239 68 68)), (Circle $rx $ry ($rr + 4 * $k)))
    $g.FillPath((Radial $dot ($rx - 2 * $k) ($ry - 3 * $k) (C 255 255 130 130) (C 255 210 34 34)), $dot)
    $g.FillPath((New-Object System.Drawing.SolidBrush (C 150 255 255 255)), (Circle ($rx - 3 * $k) ($ry - 3 * $k) (2 * $k)))
  }

  $g.Dispose()
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = $sizes | ForEach-Object { , (New-IconPng $_) }
$fs = [System.IO.File]::Create($out); $w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $d = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
  $w.Write([byte]$d); $w.Write([byte]$d); $w.Write([byte]0); $w.Write([byte]0)
  $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
  $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Close()
[System.IO.File]::WriteAllBytes((Join-Path $root 'docs\icon-256.png'), $images[-1])
[System.IO.File]::WriteAllBytes((Join-Path $root 'docs\icon-32.png'), $images[3])
Write-Output "OK $out"
