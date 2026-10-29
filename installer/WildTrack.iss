; Inno Setup script for WildTrack. Build with tools/make-installer.ps1.
#define AppName "WildTrack"
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

[Setup]
; same AppId as when the app was "GuitarCam Studio Native": the new installer upgrades that install in place
AppId={{6F1C2B7E-3D54-4A8B-9C0E-2A7B5D1E8F43}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=xtrimdr
DefaultDirName={autopf}\WildTrack
DefaultGroupName=WildTrack
UninstallDisplayIcon={app}\WildTrack.exe
SetupIconFile=..\src\WildTrack.App\Assets\WildTrack.ico
LicenseFile=..\LICENSE
OutputDir=..\artifacts
; no version in the name: the README's download button links to releases/latest/download/WildTrack-Setup.exe
OutputBaseFilename=WildTrack-Setup
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

[InstallDelete]
; what an install from before the rename left behind (exe, DLLs, VST host and shortcuts named GuitarCam)
Type: files; Name: "{app}\GuitarCam*"
Type: files; Name: "{app}\gcvsthost.exe"
Type: files; Name: "{autodesktop}\GuitarCam Studio.lnk"
Type: filesandordirs; Name: "{autoprograms}\GuitarCam Studio"

[Files]
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\WildTrack"; Filename: "{app}\WildTrack.exe"
Name: "{autodesktop}\WildTrack"; Filename: "{app}\WildTrack.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\WildTrack.exe"; Description: "{cm:LaunchProgram,WildTrack}"; Flags: nowait postinstall skipifsilent
