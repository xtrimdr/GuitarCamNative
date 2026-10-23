# Creates (or updates) the desktop shortcut "GuitarCam Studio Native" to the built GuitarCam.exe,
# with the exe's own icon. The native counterpart of the web app's launcher shortcut: the exe is the
# whole app, so there is nothing to start or stop around it.
#   tools\make-shortcut.ps1                 -> Debug build
#   tools\make-shortcut.ps1 -Configuration Release
param([string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "src\GuitarCam.App\bin\$Configuration\net10.0-windows\GuitarCam.exe"
if (-not (Test-Path $exe)) { throw "No encontré $exe. Compilá primero (dotnet build -c $Configuration)." }
$desktop = [Environment]::GetFolderPath('Desktop')
# The shortcut's name before the app was called "GuitarCam Studio Native"
$old = Join-Path $desktop 'GuitarCam Studio (nativa).lnk'
if (Test-Path $old) { Remove-Item $old }
$lnk = Join-Path $desktop 'GuitarCam Studio Native.lnk'
$s = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk)
$s.TargetPath = $exe
$s.WorkingDirectory = Split-Path -Parent $exe
$s.IconLocation = "$exe,0"
$s.Description = 'GuitarCam Studio Native'
$s.Save()
"Listo: $lnk"
