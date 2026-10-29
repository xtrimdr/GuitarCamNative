# Makes the WildTrack icon and writes a multi-size .ico, the title bar's 64 px logo and the PNGs in docs\.
# A camera iris of brushed steel blades around the sensor, with the most recognizable part of an instrument lightly
# engraved on each blade, like a close-up in a film (guitar pickups, piano keys, microphone grille, sax bell,
# violin f-holes, trumpet valves). The lens is rendered in Blender and laid on a dark rounded square here.
#   1. Inkscape rasterizes the drawings in tools\icon\instrumentos (public domain, see ORIGEN.txt)
#   2. tools\icon\relieves.py (Python with numpy and OpenCV) turns them into engraving maps
#   3. tools\icon\escena.py renders the lens in Blender: big with the engravings, and bare for the small sizes
# Everything in between goes to artifacts\icon (not in git).
#   tools\make-icon.ps1               -> renders and writes the icon (a minute or so)
#   tools\make-icon.ps1 -SinRender    -> only lays the last renders out again (to tweak the background or sizes)
param([switch]$SinRender)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'src\WildTrack.App\Assets'
$work = Join-Path $root 'artifacts\icon'
$big = Join-Path $work 'lente.png'
$bare = Join-Path $work 'lente-plano.png'
# The engraved lens is used from this size up; below it the bare one, where engravings would be noise
$EngravedFrom = 128
# Render sizes of the two lenses
$BigRender = 1024
$BareRender = 256

# Runs an outside program and returns its output: Windows PowerShell turns anything it writes to stderr (Inkscape's
# and Blender's harmless warnings) into an error under 'Stop', so its result is checked by the caller instead
function Run([string]$exe) {
  $ErrorActionPreference = 'Continue'
  & $exe @args 2>&1 | ForEach-Object { "$_" }
}

function Need([string]$what, [string]$path, [string]$how) {
  if (-not $path -or -not (Test-Path $path)) { throw "Falta $what. $how" }
  $path
}

if (-not $SinRender) {
  $inkscape = Need 'Inkscape' 'C:\Program Files\Inkscape\bin\inkscape.com' 'Instalalo con: winget install Inkscape.Inkscape'
  $blender = Get-ChildItem 'C:\Program Files\Blender Foundation\*\blender.exe' -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
  $blender = Need 'Blender' $blender 'Instalalo con: winget install BlenderFoundation.Blender'
  $python = (Get-Command python -ErrorAction SilentlyContinue | Where-Object Source -notmatch 'WindowsApps' | Select-Object -First 1).Source
  $python = Need 'Python' $python 'Instalá Python 3 y después: python -m pip install numpy opencv-python'
  $null = Run $python -c 'import cv2, numpy'
  if ($LASTEXITCODE -ne 0) { throw 'Python no tiene OpenCV. Instalalo con: python -m pip install numpy opencv-python' }

  $raster = Join-Path $work 'raster'; $maps = Join-Path $work 'relieves'
  New-Item -ItemType Directory -Force $raster, $maps | Out-Null
  Write-Host 'Rasterizando los dibujos...' -ForegroundColor Cyan
  foreach ($svg in Get-ChildItem (Join-Path $PSScriptRoot 'icon\instrumentos') -Filter *.svg) {
    $png = Join-Path $raster "$($svg.BaseName).png"
    $null = Run $inkscape $svg.FullName --export-type=png --export-filename=$png --export-height=1400
    if (-not (Test-Path $png)) { throw "Inkscape no pudo rasterizar $($svg.Name)." }
  }
  Write-Host 'Armando los grabados...' -ForegroundColor Cyan
  $out = Run $python (Join-Path $PSScriptRoot 'icon\relieves.py') $raster $maps
  if ($LASTEXITCODE -ne 0) { $out | Write-Host; throw 'relieves.py falló.' }
  Write-Host 'Renderizando en Blender...' -ForegroundColor Cyan
  $scene = Join-Path $PSScriptRoot 'icon\escena.py'
  foreach ($r in @(@($big, $BigRender, @()), @($bare, $BareRender, @('--plano')))) {
    # '--' quoted: bare, PowerShell would take it as the end of Run's own parameters and drop it
    $out = Run $blender -b --factory-startup -P $scene '--' $maps $r[0] $r[1] @($r[2])
    if (-not ($out | Select-String '^OK ')) { $out | Select-Object -Last 15 | Write-Host; throw 'El render de Blender falló.' }
  }
}
$lensBig = [System.Drawing.Image]::FromFile((Need 'el render grande' $big 'Corré make-icon.ps1 sin -SinRender.'))
$lensBare = [System.Drawing.Image]::FromFile((Need 'el render chico' $bare 'Corré make-icon.ps1 sin -SinRender.'))

function C([int]$a, [int]$r, [int]$g, [int]$b) { [System.Drawing.Color]::FromArgb($a, $r, $g, $b) }

function New-IconPng([int]$s) {
  $bmp = New-Object System.Drawing.Bitmap $s, $s
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.InterpolationMode = 'HighQualityBicubic'
  $g.Clear([System.Drawing.Color]::Transparent)
  $k = $s / 256.0
  $small = $s -le 32
  # Rounded-square background, dark, with a faint top rim
  $m = if ($small) { 0 } else { 4 * $k }
  $r = 54 * $k; $w = $s - 2 * $m
  $bg = New-Object System.Drawing.Drawing2D.GraphicsPath
  $bg.AddArc($m, $m, 2 * $r, 2 * $r, 180, 90); $bg.AddArc($m + $w - 2 * $r, $m, 2 * $r, 2 * $r, 270, 90)
  $bg.AddArc($m + $w - 2 * $r, $m + $w - 2 * $r, 2 * $r, 2 * $r, 0, 90); $bg.AddArc($m, $m + $w - 2 * $r, 2 * $r, 2 * $r, 90, 90)
  $bg.CloseFigure()
  $top = New-Object System.Drawing.PointF 0, 0; $bottom = New-Object System.Drawing.PointF 0, $s
  $g.FillPath((New-Object System.Drawing.Drawing2D.LinearGradientBrush $top, $bottom, (C 255 41 41 46), (C 255 13 13 15)), $bg)
  if (-not $small) {
    $rim = New-Object System.Drawing.Drawing2D.LinearGradientBrush $top, $bottom, (C 70 255 255 255), (C 0 255 255 255)
    $g.DrawPath((New-Object System.Drawing.Pen $rim, ([Math]::Max(1.5 * $k, 1))), $bg)
  }
  # The lens, nearly edge to edge (fully at the small sizes, where every pixel counts)
  $lens = if ($s -ge $EngravedFrom) { $lensBig } else { $lensBare }
  $d = if ($small) { $s } else { [Math]::Round($s * 0.94) }
  $o = ($s - $d) / 2
  $g.DrawImage($lens, [float]$o, [float]$o, [float]$d, [float]$d)
  $g.Dispose()
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  return , $ms.ToArray()
}

$out = Join-Path $assets 'WildTrack.ico'
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
# The title bar's logo (24 px, sharp up to 250 % display scaling), and the big one of the splash and "Acerca de"
[System.IO.File]::WriteAllBytes((Join-Path $assets 'WildTrack-64.png'), $images[6])
[System.IO.File]::WriteAllBytes((Join-Path $assets 'WildTrack-512.png'), (New-IconPng 512))
$lensBig.Dispose(); $lensBare.Dispose()
Write-Output "OK $out"
