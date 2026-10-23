# Draws the GuitarCam Native icon and writes a multi-size .ico (plus a 256 px PNG to preview it).
# Same idea as the web app's launcher icon (guitar pick + camera lens + rec dot), with more depth:
# a shaded pick with a rim light, a lens with barrel, glass and reflections, and a glossy rec dot.
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

  # Guitar pick (rounded triangle, point down)
  $pick = New-Object System.Drawing.Drawing2D.GraphicsPath
  $pick.AddBezier((& $pt 128 32), (& $pt 192 32), (& $pt 228 50), (& $pt 224 96))
  $pick.AddBezier((& $pt 224 96), (& $pt 218 152), (& $pt 160 216), (& $pt 128 228))
  $pick.AddBezier((& $pt 128 228), (& $pt 96 216), (& $pt 38 152), (& $pt 32 96))
  $pick.AddBezier((& $pt 32 96), (& $pt 28 50), (& $pt 64 32), (& $pt 128 32))
  $pick.CloseFigure()
  if (-not $small) {
    # Soft shadow under the pick
    $sh = $pick.Clone(); $mx = New-Object System.Drawing.Drawing2D.Matrix; $mx.Translate(0, 6 * $k); $sh.Transform($mx)
    $g.FillPath((New-Object System.Drawing.SolidBrush (C 110 0 0 0)), $sh)
  }
  $pb = New-Object System.Drawing.Drawing2D.LinearGradientBrush (& $pt 60 32), (& $pt 190 228), (C 255 125 238 252), (C 255 8 132 166)
  $g.FillPath($pb, $pick)
  if (-not $small) {
    # Rim light along the top edge, darker toward the tip
    $rl = New-Object System.Drawing.Drawing2D.LinearGradientBrush (& $pt 0 32), (& $pt 0 228), (C 200 220 252 255), (C 0 220 252 255)
    $g.DrawPath((New-Object System.Drawing.Pen $rl, (3 * $k)), $pick)
    $g.DrawPath((New-Object System.Drawing.Pen (C 90 4 60 80), (1.2 * $k)), $pick)
  }

  # Camera lens
  $cx = 128 * $k; $cy = 110 * $k
  $g.FillPath((Radial (Circle $cx ($cy + 3 * $k) (56 * $k)) $cx ($cy + 3 * $k) (C 90 0 40 55) (C 0 0 40 55)), (Circle $cx ($cy + 3 * $k) (56 * $k)))
  $body = Circle $cx $cy (50 * $k)
  $g.FillPath((New-Object System.Drawing.Drawing2D.LinearGradientBrush (& $pt 0 60), (& $pt 0 160), (C 255 30 30 34), (C 255 9 9 11)), $body)
  if ($small) {
    $g.FillPath((New-Object System.Drawing.SolidBrush (C 255 39 39 42)), (Circle $cx $cy (28 * $k)))
  } else {
    # Metal barrel ring
    $ring = Circle $cx $cy (40 * $k)
    $g.FillPath((New-Object System.Drawing.Drawing2D.LinearGradientBrush (& $pt 0 70), (& $pt 0 150), (C 255 72 72 80), (C 255 20 20 23)), $ring)
    $g.FillPath((New-Object System.Drawing.SolidBrush (C 255 12 12 14)), (Circle $cx $cy (35 * $k)))
    # Glass with a cyan tint deep inside
    $glass = Circle $cx $cy (31 * $k)
    $g.FillPath((Radial $glass ($cx + 6 * $k) ($cy + 8 * $k) (C 255 18 70 90) (C 255 10 14 20)), $glass)
    $g.DrawPath((New-Object System.Drawing.Pen (C 60 103 232 249), (1.2 * $k)), (Circle $cx $cy (22 * $k)))
    $g.FillPath((New-Object System.Drawing.SolidBrush (C 255 6 8 10)), (Circle $cx $cy (11 * $k)))
    # Reflections: a soft arc up-left, a sharp dot, a faint one down-right
    $arc = New-Object System.Drawing.Pen (C 120 190 245 255), (4 * $k)
    $arc.StartCap = 'Round'; $arc.EndCap = 'Round'
    $g.DrawArc($arc, $cx - 24 * $k, $cy - 24 * $k, 48 * $k, 48 * $k, 200, 55)
  }
  $hr = [Math]::Max(7 * $k, 1.2)
  $g.FillPath((New-Object System.Drawing.SolidBrush (C 230 200 248 255)), (Circle ($cx - 13 * $k) ($cy - 13 * $k) $hr))
  if (-not $small) { $g.FillPath((New-Object System.Drawing.SolidBrush (C 70 200 248 255)), (Circle ($cx + 14 * $k) ($cy + 14 * $k) (4 * $k))) }

  # Rec dot with a dark cut-out ring
  $rr = [Math]::Max(17 * $k, 2.5); $rx = 180 * $k; $ry = 180 * $k
  $g.FillPath((New-Object System.Drawing.SolidBrush (C 255 11 11 13)), (Circle $rx $ry ($rr + 6 * $k)))
  $dot = Circle $rx $ry $rr
  if ($small) {
    $g.FillPath((New-Object System.Drawing.SolidBrush (C 255 239 68 68)), $dot)
  } else {
    $g.FillPath((Radial $dot ($rx - 5 * $k) ($ry - 6 * $k) (C 255 255 130 130) (C 255 200 30 30)), $dot)
    $g.FillPath((New-Object System.Drawing.SolidBrush (C 150 255 255 255)), (Circle ($rx - 6 * $k) ($ry - 7 * $k) (4 * $k)))
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
