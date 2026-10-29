# Baja el modelo de Basic Pitch (Spotify, Apache 2.0) a lib\basic-pitch, fuera de git como los DLL de FFmpeg.
# Es el que saca las notas MIDI de un clip de audio ("Extraer MIDI", AudioToMidi): nmp.onnx (~230 KB) y su licencia,
# de un commit fijo para que todos usen el mismo modelo. Correlo de nuevo con -Reemplazar para bajarlo otra vez.
param(
    [string]$Commit = 'dfb20ef559dff1792e11e022f3f0c7008c1dee6d',
    [switch]$Reemplazar
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dest = Join-Path $root 'lib\basic-pitch'
$model = Join-Path $dest 'nmp.onnx'

if ((Test-Path $model) -and -not $Reemplazar) {
    Write-Host "Ya está: $model (usá -Reemplazar para bajarlo de nuevo)"
    exit 0
}
New-Item -ItemType Directory -Force $dest | Out-Null
$base = "https://raw.githubusercontent.com/spotify/basic-pitch/$Commit"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Invoke-WebRequest "$base/basic_pitch/saved_models/icassp_2022/nmp.onnx" -OutFile $model -UseBasicParsing
Invoke-WebRequest "$base/LICENSE" -OutFile (Join-Path $dest 'LICENSE-BasicPitch.txt') -UseBasicParsing
if ((Get-Item $model).Length -lt 100KB) { throw 'El modelo bajó incompleto.' }
Write-Host "Modelo de Basic Pitch en $dest" -ForegroundColor Green
