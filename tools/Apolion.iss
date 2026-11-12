; Apolion Browser — Inno Setup installer script
; Installs per-user to %LOCALAPPDATA%\Apolion\ — no admin elevation required.
;
; To compile:
;   iscc tools\Apolion.iss
;
; Requires Inno Setup (https://jrsoftware.org). If iscc is not on PATH, use
; the full path to the Inno Setup compiler (ISCC.exe).

#define AppName "Apolion"
#define AppVersion "1.0.1"
#define AppPublisher "Apolion Browser"
#define AppURL "https://github.com/Random-glitch123/Apolion"
#define InstallSubDir "Apolion"

; ════════════════════════════════════════════════════════════════════════════════
; Setup section
; ════════════════════════════════════════════════════════════════════════════════

[Setup]
AppId={{{E1A2B3C4-D5E6-4F7A-8B9C-0D1E2F3A4B5C}}}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}
DefaultDirName={localappdata}\{#InstallSubDir}
DefaultGroupName={#AppName}
AllowNoIcons=yes
LicenseFile=EULA.md
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=no
ChangesEnvironment=yes
UninstallDisplayIcon={app}\Apolion.exe
Compression=lzma
SolidCompression=yes
OutputDir=artifacts\release
OutputBaseFilename=Apolion-{#AppVersion}-setup
SetupIconFile=..\apolion.ico
UninstallIconFile={app}\Apolion.exe
ChangesAssociations=yes

; ════════════════════════════════════════════════════════════════════════════════
; Languages
; ════════════════════════════════════════════════════════════════════════════════

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

; ════════════════════════════════════════════════════════════════════════════════
; Tasks
; ════════════════════════════════════════════════════════════════════════════════

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

; ════════════════════════════════════════════════════════════════════════════════
; Files — the published app output goes here
; ════════════════════════════════════════════════════════════════════════════════

[Files]
; Source is the publish output directory (set by the build script via /S or preprocessor)
; Each file is installed to the app folder. Shared CefSharp DLLs are not registered
; as they are private to this application.
Source: "..\publish-staging\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; EULA.md is shipped as a reference file (the app no longer reads it at runtime)
Source: "..\EULA.md"; DestDir: "{app}"; Flags: ignoreversion

; ════════════════════════════════════════════════════════════════════════════════
; Icons
; ════════════════════════════════════════════════════════════════════════════════

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\Apolion.exe"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{commondesktop}\{#AppName}"; Filename: "{app}\Apolion.exe"; Tasks: desktopicon

; ════════════════════════════════════════════════════════════════════════════════
; Run
; ════════════════════════════════════════════════════════════════════════════════

[Run]
Filename: "{app}\Apolion.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

; ════════════════════════════════════════════════════════════════════════════════
; Uninstall delete
; ════════════════════════════════════════════════════════════════════════════════

[UninstallDelete]
; Remove the whole app folder on uninstall. User data in %APPDATA%\Apolion and
; %LOCALAPPDATA%\Apolion (settings, tabs, CEF profile, logs) is intentionally
; left untouched so reinstalling doesn't lose user data.
Type: filesandordirs; Name: "{app}"
