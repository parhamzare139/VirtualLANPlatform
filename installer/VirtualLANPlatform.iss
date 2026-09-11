#define AppName      "Virtual LAN Platform"
#define AppVersion   "1.0.7"
#define AppPublisher "VirtualLAN"
#define AppExeName   "VirtualLANPlatform.exe"
#define SourceDir    "..\publish"
; Repo root — so the installer sits right next to README.md, visible and
; individually downloadable without digging into a subfolder.
#define OutputDir    ".."

[Setup]
AppId={{F3A2B9C1-4E7D-4F2A-8B3C-9D1E5F6A7B8C}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisherURL=https://github.com/
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
AllowNoIcons=yes
OutputDir={#OutputDir}
OutputBaseFilename=VirtualLANPlatform_Setup_v{#AppVersion}
SetupIconFile=..\src\VirtualLANPlatform\Assets\logo.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
MinVersion=10.0.17763
; Windows 10 1809+ required for WinTun

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
; Main executable and all runtime files
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; wintun.dll must be in app root (P/Invoke loads by name)
Source: "{#SourceDir}\wintun.dll"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}";          Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{commondesktop}\{#AppName}";  Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""VirtualLAN Platform"""; Flags: runhidden
Filename: "netsh"; Parameters: "advfirewall firewall add rule name=""VirtualLAN Platform"" protocol=UDP dir=in localport=42777 action=allow"; Flags: runhidden
Filename: "{app}\{#AppExeName}"; Description: "اجرای {#AppName}"; Flags: nowait postinstall skipifsilent shellexec

[UninstallDelete]
; Remove app data only if user agrees (handled by app itself on uninstall)
Type: filesandordirs; Name: "{app}"

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
end;
