# The one way to try a change: builds the solution, runs gclab's DSP checks, copies the app to its own folder
# and opens that copy. The app never runs from bin\, so an open GuitarCam never blocks the next build, and every
# copy carries a prueba.txt mark (commit, uncommitted changes, time) that the app shows in its title bar.
#   tools\probar.ps1 "qué probar"            -> build, check, copy and open with that test note
#   tools\probar.ps1 "qué probar" -Proyecto D:\...\MiTema   -> same, opening that project directly
#   tools\probar.ps1 -Abrir                  -> open the newest copy without building (the desktop shortcut)
#   tools\probar.ps1 -SinComprobar           -> skip gclab comprobar-dsp (UI-only changes in a hurry)
#   tools\probar.ps1 -NoAbrir                -> build, check and copy only (what the agent runs after each change)
# Before opening, if a GuitarCam is already open, a dialog asks whether to close it (it saves first), so the new copy
# does not open the same project read-only; one open from bin\ is asked about before building, since it blocks it.
param(
    [Parameter(Position = 0)][string]$Que = '',
    [string]$Proyecto = '',
    [switch]$Abrir,
    [switch]$SinComprobar,
    [switch]$NoAbrir
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$bin = Join-Path $root 'src\GuitarCam.App\bin\Debug\net10.0-windows'
$copies = Join-Path $env:LOCALAPPDATA 'GuitarCam Pruebas'
# How many copies stay around; older ones are deleted (unless one is still open)
$Keep = 5

function Fail([string]$text) {
    Write-Host ''
    Write-Host "NO SE PUEDE PROBAR: $text" -ForegroundColor Red
    exit 1
}

# How long a GuitarCam asked to close gets to save and close before the script gives up
$CloseWaitSec = 20

# Asks in a dialog (the agent's console cannot be answered) whether to close the GuitarCam windows that are open.
# Closing goes through the window's own close, so the project is saved and a take in progress is kept; an export in
# progress refuses to close. Returns 'closed', 'keep' (open the new one anyway) or 'cancel'.
function AskClose($procs, [string]$why, [bool]$canKeep) {
    Add-Type -AssemblyName System.Windows.Forms
    $names = ($procs | ForEach-Object { if ($_.MainWindowTitle) { $_.MainWindowTitle } else { 'GuitarCam' } }) -join "`n"
    $text = "Tenés GuitarCam abierto:`n$names`n`n$why`n`n¿Lo cierro? (guarda el proyecto antes de cerrar)"
    if ($canKeep) { $text += "`n`nSí: lo cierro y abro la nueva.`nNo: abro la nueva igual (si es el mismo proyecto, en solo lectura).`nCancelar: no abro nada." }
    $buttons = if ($canKeep) { 'YesNoCancel' } else { 'YesNo' }
    $owner = New-Object System.Windows.Forms.Form -Property @{ TopMost = $true; ShowInTaskbar = $false }
    $answer = [System.Windows.Forms.MessageBox]::Show($owner, $text, 'GuitarCam · Probar', $buttons, 'Question')
    $owner.Dispose()
    if ($answer -eq 'Cancel' -or (-not $canKeep -and $answer -eq 'No')) { return 'cancel' }
    if ($answer -eq 'No') { return 'keep' }
    foreach ($p in $procs) { [void]$p.CloseMainWindow() }
    foreach ($p in $procs) {
        if (-not $p.WaitForExit($CloseWaitSec * 1000)) {
            Fail "GuitarCam no se cerró en $CloseWaitSec s (¿está exportando o esperando algo?). Cerralo a mano y volvé a correr esto."
        }
    }
    Write-Host 'GuitarCam cerrado.' -ForegroundColor DarkGray
    return 'closed'
}

function Launch([string]$dir) {
    $open = @(Get-Process GuitarCam -ErrorAction SilentlyContinue)
    if ($open.Count -and (AskClose $open 'Voy a abrir la versión nueva para probar.' $true) -eq 'cancel') {
        Write-Host 'No abrí nada: elegiste cancelar.' -ForegroundColor Yellow
        exit 2
    }
    $a = @()
    if ($Proyecto) { $a += $Proyecto }
    if ($Que) { $a += '--probar'; $a += $Que }
    $exe = Join-Path $dir 'GuitarCam.exe'
    if ($a.Count) { Start-Process -FilePath $exe -WorkingDirectory $dir -ArgumentList ($a | ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' }) }
    else { Start-Process -FilePath $exe -WorkingDirectory $dir }
    Write-Host "Abierto: $exe" -ForegroundColor Green
}

if ($Abrir) {
    $last = Get-ChildItem $copies -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
    if (-not $last) { Fail 'todavía no hay ninguna copia de prueba. Corré tools\probar.ps1 una vez.' }
    Launch $last.FullName
    exit 0
}

# An app opened straight from bin\ (an old shortcut, or by hand) locks the DLLs and the build fails
$fromBin = @(Get-Process GuitarCam -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($bin, 'OrdinalIgnoreCase') })
if ($fromBin.Count -and (AskClose $fromBin 'Está abierto desde la carpeta de compilación y no me deja compilar.' $false) -ne 'closed') {
    Fail 'hay un GuitarCam abierto desde bin\ y no lo cerré. Cerralo y volvé a correr esto.'
}

Push-Location $root
try {
    $commit = (git rev-parse --short HEAD).Trim()
    $dirty = [bool](git status --porcelain)
} finally { Pop-Location }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
# prueba.txt: commit, "dirty" or "clean", build time (ISO); the app formats it in its own language
$mark = "$commit$(if ($dirty) { ' + cambios sin commit' })"

Write-Host "Compilando ($mark)..." -ForegroundColor Cyan
dotnet build (Join-Path $root 'GuitarCamNative.slnx') -c Debug -nologo -v:minimal -clp:ErrorsOnly
if ($LASTEXITCODE -ne 0) { Fail 'la compilación falló (arriba están los errores).' }

if (-not $SinComprobar) {
    Write-Host 'Comprobando el DSP (gclab comprobar-dsp)...' -ForegroundColor Cyan
    $out = & (Join-Path $root 'src\GuitarCam.Lab\bin\Debug\net10.0-windows\gclab.exe') comprobar-dsp 2>&1
    if ($LASTEXITCODE -ne 0) { $out | Write-Host; Fail "gclab comprobar-dsp falló (código $LASTEXITCODE)." }
    Write-Host '  todo bien' -ForegroundColor DarkGray
}

$dest = Join-Path $copies "$stamp-$commit"
New-Item -ItemType Directory -Force $dest | Out-Null
# runtimes\ holds native libraries for every OS (~120 MB); the app only needs win-x64's
Get-ChildItem $bin | Where-Object Name -ne 'runtimes' | Copy-Item -Destination $dest -Recurse -Force
$native = Join-Path $bin 'runtimes\win-x64'
if (Test-Path $native) {
    New-Item -ItemType Directory -Force (Join-Path $dest 'runtimes') | Out-Null
    Copy-Item $native (Join-Path $dest 'runtimes') -Recurse -Force
}
Set-Content -Path (Join-Path $dest 'prueba.txt') -Value @($commit, $(if ($dirty) { 'dirty' } else { 'clean' }), (Get-Date -Format 's')) -Encoding utf8

Get-ChildItem $copies -Directory | Sort-Object Name -Descending | Select-Object -Skip $Keep | ForEach-Object {
    try { Remove-Item $_.FullName -Recurse -Force -ErrorAction Stop } catch { } # still open: next time
}

Write-Host "Copia: $dest" -ForegroundColor DarkGray
if (-not $NoAbrir) { Launch $dest }
