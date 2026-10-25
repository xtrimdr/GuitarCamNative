# Creates (or updates) the desktop shortcut "GuitarCam Studio Native". It opens the newest test copy made by
# tools\probar.ps1 (probar.ps1 -Abrir), never the exe in bin\: an app running from bin\ locks the DLLs and the next
# build fails without anyone noticing.
#   tools\make-shortcut.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$script = Join-Path $PSScriptRoot 'probar.ps1'
$icon = Join-Path $root 'src\GuitarCam.App\Assets\GuitarCam.ico'
$desktop = [Environment]::GetFolderPath('Desktop')
# The shortcut's name before the app was called "GuitarCam Studio Native"
$old = Join-Path $desktop 'GuitarCam Studio (nativa).lnk'
if (Test-Path $old) { Remove-Item $old }
$lnk = Join-Path $desktop 'GuitarCam Studio Native.lnk'
$s = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk)
$s.TargetPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$s.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$script`" -Abrir"
$s.WorkingDirectory = $root
$s.IconLocation = "$icon,0"
$s.WindowStyle = 7 # minimized: the console does not flash
$s.Description = 'GuitarCam Studio Native (la última copia de prueba)'
$s.Save()
"Listo: $lnk"
