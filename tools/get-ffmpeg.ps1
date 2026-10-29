# Baja los DLL de FFmpeg 9 (versión shared, LGPL) a lib\ffmpeg, fuera de git: los usa la preview para decodificar
# dentro del proceso (NativeVideoSource). Del build de BtbN de la rama 9.0 solo copia lo que carga la app y la licencia.
# Las DLL no cambian de nombre dentro de la rama, así que "latest" sirve. Correlo de nuevo con -Reemplazar para bajarlos otra vez.
param(
    [string]$Url = 'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n9.0-latest-win64-lgpl-shared-9.0.zip',
    [switch]$Reemplazar
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dest = Join-Path $root 'lib\ffmpeg'
# lo que carga FFmpeg.AutoGen (NativeVideoSource)
$dlls = 'avcodec-63', 'avformat-63', 'avutil-61', 'swscale-10', 'swresample-7'

if ((Test-Path (Join-Path $dest 'avcodec-63.dll')) -and -not $Reemplazar) {
    Write-Host "Ya están: $dest (usá -Reemplazar para bajarlos de nuevo)"
    exit 0
}
$tmp = Join-Path ([IO.Path]::GetTempPath()) "gc-ffmpeg-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force $tmp | Out-Null
try {
    $zip = Join-Path $tmp 'ffmpeg.zip'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $ProgressPreference = 'SilentlyContinue'  # la barra de progreso de 5.1 hace la descarga diez veces más lenta
    Invoke-WebRequest $Url -OutFile $zip -UseBasicParsing
    Expand-Archive $zip (Join-Path $tmp 'x')
    $top = Get-ChildItem (Join-Path $tmp 'x') -Directory | Select-Object -First 1
    New-Item -ItemType Directory -Force $dest | Out-Null
    foreach ($d in $dlls) {
        $src = Join-Path $top.FullName "bin\$d.dll"
        if (-not (Test-Path $src)) { throw "El zip no trae $d.dll: ¿cambió la versión de FFmpeg?" }
        Copy-Item $src $dest -Force
    }
    Copy-Item (Join-Path $top.FullName 'LICENSE.txt') (Join-Path $dest 'LICENSE-FFmpeg.txt') -Force
}
finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host "DLL de FFmpeg en $dest" -ForegroundColor Green
