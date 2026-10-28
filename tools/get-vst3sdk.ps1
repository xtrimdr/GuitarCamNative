# Baja el SDK de VST3 de Steinberg (licencia MIT desde la 3.8) a lib\vst3sdk, fuera de git como los DLL de FFmpeg.
# Solo lo que usa el puente de plugins (src\GuitarCam.VstHost): pluginterfaces, base y public.sdk, en un clon
# superficial de una versión fija. Correlo de nuevo con -Reemplazar para cambiar de versión.
param(
    [string]$Version = 'v3.8.1_build_84',
    [switch]$Reemplazar
)
# git escribe su progreso en stderr: en Windows PowerShell 5.1, 'Stop' lo tomaría como un error; se miran los códigos de salida
$root = Split-Path $PSScriptRoot -Parent
$dest = Join-Path $root 'lib\vst3sdk'

if (Test-Path (Join-Path $dest 'pluginterfaces\base') -and -not $Reemplazar) {
    Write-Host "Ya está: $dest (usá -Reemplazar para bajarlo de nuevo)"
    exit 0
}
# lo que haya (un clon a medias, otra versión) se va antes de clonar
if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
New-Item -ItemType Directory -Force (Split-Path $dest -Parent) | Out-Null

git clone --depth 1 --branch $Version https://github.com/steinbergmedia/vst3sdk $dest
if ($LASTEXITCODE -ne 0) { throw "No pude clonar el SDK ($Version)." }
Push-Location $dest
try {
    git submodule update --init --depth 1 base pluginterfaces public.sdk
    if ($LASTEXITCODE -ne 0) { throw 'No pude bajar los submódulos del SDK.' }
}
finally { Pop-Location }
Write-Host "SDK de VST3 $Version en $dest" -ForegroundColor Green
