; Inno Setup script for GuitarCam Studio Native. Build with tools/make-installer.ps1.
#define AppName "GuitarCam Studio Native"
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

[Setup]
AppId={{6F1C2B7E-3D54-4A8B-9C0E-2A7B5D1E8F43}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=xtrimdr
DefaultDirName={autopf}\GuitarCam Studio
DefaultGroupName=GuitarCam Studio
UninstallDisplayIcon={app}\GuitarCam.exe
SetupIconFile=..\src\GuitarCam.App\Assets\GuitarCam.ico
LicenseFile=..\LICENSE
OutputDir=..\artifacts
OutputBaseFilename=GuitarCamStudio-Setup-{#AppVersion}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequiredOverridesAllowed=dialog
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\GuitarCam Studio"; Filename: "{app}\GuitarCam.exe"
Name: "{autodesktop}\GuitarCam Studio"; Filename: "{app}\GuitarCam.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\GuitarCam.exe"; Description: "{cm:LaunchProgram,GuitarCam Studio}"; Flags: nowait postinstall skipifsilent
