# Draws the GuitarCam Studio Native icon and writes a multi-size .ico (plus a 256 px PNG to preview it).
# A camera aperture whose blades are guitar picks, with the rec light in the opening: the pick and the
# camera as a single object, in the spirit of the GarageBand / Logic Pro icons.
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

  # Aperture iris whose six blades are guitar picks. Each blade is a pick drawn around the origin
  # (round end up, tip down) and moved out from the centre so its tip points into the opening.
  $cx = 128 * $k; $cy = 128 * $k
  if (-not $small) {
    $halo = Circle $cx $cy (112 * $k)
    $g.FillPath((Radial $halo $cx $cy (C 60 34 211 238) (C 0 34 211 238)), $halo)
  }
  $ps = 102 * $k
  $blade = New-Object System.Drawing.Drawing2D.GraphicsPath
  foreach ($q in @(
      @(0.5, 0.0, 0.69, 0.0, 0.84, 0.02, 0.9, 0.11),
      @(0.9, 0.11, 0.96, 0.2, 0.86, 0.48, 0.6, 0.77),
      @(0.6, 0.77, 0.55, 0.83, 0.45, 0.83, 0.4, 0.77),
      @(0.4, 0.77, 0.14, 0.48, 0.04, 0.2, 0.1, 0.11),
      @(0.1, 0.11, 0.16, 0.02, 0.31, 0.0, 0.5, 0.0))) {
    $bp = for ($j = 0; $j -lt 8; $j += 2) { New-Object System.Drawing.PointF (($q[$j] - 0.5) * $ps), (($q[$j + 1] - 0.4) * $ps) }
    $blade.AddBezier($bp[0], $bp[1], $bp[2], $bp[3])
  }
  $blade.CloseFigure()
  $place = {
    param($i, [float]$extra)
    $mx = New-Object System.Drawing.Drawing2D.Matrix
    $mx.Translate($cx, $cy); $mx.Rotate($i * 60 + $extra); $mx.Translate(22.5 * $k, -50 * $k); $mx.Rotate(-28); $mx
  }
  $drawBlade = {
    param($i)
    if (-not $small) {
      # Soft shadow cast onto the blade underneath (counter-clockwise neighbour)
      foreach ($a in -1.5, -3, -4.5) {
        $sp = $blade.Clone(); $sp.Transform((& $place $i $a))
        $g.FillPath((New-Object System.Drawing.SolidBrush (C 45 0 0 0)), $sp)
      }
    }
    $mx = & $place $i 0
    $p = $blade.Clone(); $p.Transform($mx)
    # Light on the round end, deep toward the tip in the centre
    $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, (-0.45 * $ps)), (New-Object System.Drawing.PointF 0, (0.45 * $ps)), (C 255 130 238 252), (C 255 6 112 150)
    $br.Transform = $mx
    $g.FillPath($br, $p)
    if (-not $small) {
      # Rim light just inside the round end: a stroke on a slightly shrunken copy, so nothing spills out
      # (a clip would do it too, but GDI+ clips without anti-aliasing)
      $inner = $blade.Clone(); $sm = New-Object System.Drawing.Drawing2D.Matrix; $sm.Scale(0.96, 0.96); $inner.Transform($sm); $inner.Transform($mx)
      # The brush spans the whole blade (GDI+ tiles a gradient past its ends) and fades out halfway down
      $rl = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, (-0.5 * $ps)), (New-Object System.Drawing.PointF 0, (0.5 * $ps)), (C 0 0 0 0), (C 0 0 0 0)
      $cb = New-Object System.Drawing.Drawing2D.ColorBlend 4
      $cb.Colors = @((C 150 235 253 255), (C 150 235 253 255), (C 0 235 253 255), (C 0 235 253 255)); $cb.Positions = @(0, 0.12, 0.42, 1)
      $rl.InterpolationColors = $cb
      $rl.Transform = $mx
      $g.DrawPath((New-Object System.Drawing.Pen $rl, (1.6 * $k)), $inner)
    }
  }
  for ($i = 0; $i -lt 6; $i++) { & $drawBlade $i }
  # Close the ring: the first blade goes back on top of the last one, only on that side
  $wedge = New-Object System.Drawing.Drawing2D.GraphicsPath
  $wedge.AddPie($cx - 130 * $k, $cy - 130 * $k, 260 * $k, 260 * $k, -150, 84)
  $g.SetClip($wedge)
  & $drawBlade 0
  $g.ResetClip()

  # The opening: dark glass with the rec light in the middle
  $g.FillPath((New-Object System.Drawing.SolidBrush (C 255 8 10 12)), (Circle $cx $cy (22 * $k)))
  if (-not $small) { $g.DrawPath((New-Object System.Drawing.Pen (C 70 103 232 249), (1.2 * $k)), (Circle $cx $cy (18.5 * $k))) }
  $rr = if ($small) { 17 * $k } else { 12.5 * $k }
  $dot = Circle $cx $cy $rr
  if ($small) {
    $g.FillPath((New-Object System.Drawing.SolidBrush (C 255 239 68 68)), $dot)
  } else {
    $glow = Circle $cx $cy ($rr + 7 * $k)
    $g.FillPath((Radial $glow $cx $cy (C 120 239 68 68) (C 0 239 68 68)), $glow)
    $g.FillPath((Radial $dot ($cx - 4 * $k) ($cy - 5 * $k) (C 255 255 135 135) (C 255 200 28 28)), $dot)
    $g.FillPath((New-Object System.Drawing.SolidBrush (C 160 255 255 255)), (Circle ($cx - 5 * $k) ($cy - 5 * $k) (3.2 * $k)))
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
