# Publishes the app self-contained and builds the Inno Setup installer into artifacts\.
# Needs Inno Setup 6 (winget install JRSoftware.InnoSetup). Usage: .\tools\make-installer.ps1
# The version comes from Directory.Build.props (-Version overrides it, for a one-off build).
param([string]$Version)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    if (-not $Version) { $Version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version }
    if (-not $Version) { throw "no Version in Directory.Build.props" }
    # a clean folder so files from an older publish don't end up in the installer
    if (Test-Path artifacts\publish) { Remove-Item artifacts\publish -Recurse -Force }
    dotnet publish src\GuitarCam.App -c Release -r win-x64 --self-contained true -o artifacts\publish "-p:Version=$Version"
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
    $iscc = (Get-Command iscc -ErrorAction SilentlyContinue).Source
    if (-not $iscc) {
        $iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    }
    if (-not $iscc) { throw "Inno Setup 6 not found (winget install JRSoftware.InnoSetup)" }
    & $iscc "/DAppVersion=$Version" installer\GuitarCam.iss
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }
} finally { Pop-Location }
