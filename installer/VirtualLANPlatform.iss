#define AppName      "Virtual LAN Platform"
#define AppVersion   "1.0.8"
#define AppPublisher "VirtualLAN"
#define AppExeName   "VirtualLANPlatform.exe"
#define SourceDir    "..\publish"
; Repo root — so the installer sits right next to README.md, visible and
; individually downloadable without digging into a subfolder.
#define OutputDir    ".."

; The app is WPF, so it needs the .NET Desktop Runtime — not the ASP.NET or the
; plain console one. This evergreen alias always points at the current 8.0 patch.
#define DotNetUrl    "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe"
#define VlanPort     "42778"
#define RoomPort     "42777"

[Setup]
AppId={{F3A2B9C1-4E7D-4F2A-8B3C-9D1E5F6A7B8C}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisherURL=https://github.com/parhamzare139/VirtualLANPlatform
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
AllowNoIcons=yes
OutputDir={#OutputDir}
OutputBaseFilename=VirtualLANPlatform_Setup_v{#AppVersion}
SetupIconFile=..\src\VirtualLANPlatform\Assets\logo.ico
Compression=lzma2/ultra64
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
MinVersion=10.0.17763
; Windows 10 1809+ required for WinTun

; ── Appearance ──────────────────────────────────────────────────────────────
WizardStyle=modern
WizardSizePercent=120
WizardImageFile=wizard-large.bmp
WizardSmallImageFile=wizard-small.bmp
WizardImageStretch=no
DisableWelcomePage=no
ShowLanguageDialog=no
DisableReadyPage=no
DisableProgramGroupPage=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
english.PrereqGroup=Prerequisites:
english.InstallDotNet=Install .NET 8 Desktop Runtime (required to run this app)
english.DotNetTitle=Downloading .NET 8 Desktop Runtime
english.DotNetDesc=Setup is fetching the runtime this app needs. This happens once.
english.RemoveData=Also delete my settings and saved data

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"
; Only offered when the runtime is actually missing — Check hides the row entirely
; otherwise, so a user who already has it is never asked a pointless question.
; Ticked by default (no "unchecked" flag) so the common path is one click.
Name: "dotnet"; Description: "{cm:InstallDotNet}"; GroupDescription: "{cm:PrereqGroup}"; \
    Check: NeedsDotNet

[Files]
; Main executable and all runtime files
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; wintun.dll must be in app root (P/Invoke loads by name)
Source: "{#SourceDir}\wintun.dll"; DestDir: "{app}"; Flags: ignoreversion
; Downloaded at run time when the runtime task is selected; "external" means the
; compiler does not expect it to exist now.
Source: "{tmp}\windowsdesktop-runtime.exe"; DestDir: "{tmp}"; \
    Flags: external deleteafterinstall; Check: WillInstallDotNet

[Icons]
Name: "{group}\{#AppName}";           Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{commondesktop}\{#AppName}";   Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; Runtime first — the app cannot start without it.
Filename: "{tmp}\windowsdesktop-runtime.exe"; \
    Parameters: "/install /quiet /norestart"; \
    StatusMsg: "Installing .NET 8 Desktop Runtime..."; \
    Flags: waituntilterminated; Check: WillInstallDotNet

; Firewall: both ports the app listens on. Deleted first so reinstalling replaces
; the rules instead of stacking duplicates.
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""VirtualLAN Platform"""; Flags: runhidden
Filename: "netsh"; Parameters: "advfirewall firewall add rule name=""VirtualLAN Platform"" protocol=UDP dir=in localport={#RoomPort} action=allow"; Flags: runhidden
Filename: "netsh"; Parameters: "advfirewall firewall add rule name=""VirtualLAN Platform"" protocol=UDP dir=in localport={#VlanPort} action=allow"; Flags: runhidden

Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; \
    Flags: nowait postinstall skipifsilent shellexec

[UninstallRun]
; Undo everything the app added to the system, not just its own folder. Without
; this an uninstall leaves a phantom network adapter and four firewall rules
; behind with nothing left to explain them.
;
; The adapter goes first: only the app can remove a Wintun adapter, and it has to
; happen while its files are still on disk.
Filename: "{app}\{#AppExeName}"; Parameters: "--remove-adapter"; \
    Flags: runhidden waituntilterminated; RunOnceId: "RemoveAdapter"
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""VirtualLAN Platform"""; \
    Flags: runhidden; RunOnceId: "FwMain"
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""VirtualLANPlatform UDP"""; \
    Flags: runhidden; RunOnceId: "FwUdp"
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""VirtualLANPlatform VLAN"""; \
    Flags: runhidden; RunOnceId: "FwVlan"
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""VirtualLANPlatform VLAN Subnet"""; \
    Flags: runhidden; RunOnceId: "FwSubnet"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
var
  DotNetMissing:  Boolean;
  DownloadPage:   TDownloadWizardPage;

{ ── .NET detection ──────────────────────────────────────────────────────────
  Checked by directory rather than registry: the registry key documented for this
  (SOFTWARE\dotnet\Setup\InstalledVersions) is absent on plenty of machines that
  do have the runtime, including ones where it arrived via Windows Update. The
  shared framework folder is the thing the host actually resolves against. }
function IsDotNet8DesktopInstalled: Boolean;
var
  FindRec: TFindRec;
  Base:    String;
  Found:   Boolean;
begin
  Found := False;
  Base := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if DirExists(Base) then
  begin
    if FindFirst(Base + '\*', FindRec) then
    begin
      try
        repeat
          if ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and
             (Copy(FindRec.Name, 1, 2) = '8.') then
            Found := True;
        until Found or (not FindNext(FindRec));
      finally
        FindClose(FindRec);
      end;
    end;
  end;
  Result := Found;
end;

function NeedsDotNet: Boolean;
begin
  Result := DotNetMissing;
end;

{ True only when the runtime is missing AND the user left the task ticked. }
function WillInstallDotNet: Boolean;
begin
  Result := DotNetMissing and WizardIsTaskSelected('dotnet');
end;

function InitializeSetup: Boolean;
begin
  DotNetMissing := not IsDotNet8DesktopInstalled;
  Result := True;
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(
    ExpandConstant('{cm:DotNetTitle}'), ExpandConstant('{cm:DotNetDesc}'), nil);
  DownloadPage.ShowBaseNameInsteadOfUrl := True;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpReady) and WillInstallDotNet then
  begin
    DownloadPage.Clear;
    { No hash: the aka.ms alias tracks the newest 8.0 patch, so any pinned digest
      would go stale and start failing installs. The URL is Microsoft's own over
      HTTPS, which is what the redirect is there to guarantee. }
    DownloadPage.Add('{#DotNetUrl}', 'windowsdesktop-runtime.exe', '');
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
      except
        { A failed download must not abort the install: the app files are still
          worth writing, and the user can install the runtime themselves. }
        SuppressibleMsgBox(
          'Could not download the .NET 8 Desktop Runtime.' + #13#10#13#10 +
          'Setup will continue. Install it manually from:' + #13#10 +
          'https://dotnet.microsoft.com/download/dotnet/8.0',
          mbInformation, MB_OK, IDOK);
        DotNetMissing := False;
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
end;

(* ── Uninstall ───────────────────────────────────────────────────────────────
   Settings live outside the program folder, so removing it does not touch them.
   Deleting someone's saved identity without asking is the wrong default, so ask
   and default to No. Pascal { } comments are avoided here: an Inno constant such
   as {app} inside one closes the comment at its brace. *)
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    if SuppressibleMsgBox(
         'Also delete your settings (username, port, saved volumes and the virtual LAN id)?',
         mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
    begin
      DataDir := ExpandConstant('{localappdata}\VirtualLANPlatform');
      if DirExists(DataDir) then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;
