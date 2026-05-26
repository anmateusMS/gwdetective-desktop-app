; ─── GW Detective installer (Inno Setup 6) ──────────────────────────────────
; Compile with:
;   ISCC.exe /DARCH=x64    GWDetective.iss
;   ISCC.exe /DARCH=arm64  GWDetective.iss
; Produces:  installer\Output\GWDetective-Setup-<arch>.exe
;
; What this installer does:
;   1. Copies the matching architecture's portable publish output
;      (GWDetective.exe + web\) under %LOCALAPPDATA%\Programs\GWDetective.
;   2. Creates a Start-menu shortcut.
;   3. Detects if Microsoft Edge WebView2 Runtime is missing; if so, runs
;      the bundled Evergreen Bootstrapper silently to install it. The
;      bootstrapper itself will elevate if needed.
;   4. Adds a proper "Apps & Features" uninstall entry.
;
; Per-user install (no admin), so end users don't need elevation for the
; app itself. WebView2 bootstrapper handles its own UAC prompt.

#ifndef ARCH
  #define ARCH "x64"
#endif

#define MyAppName      "GW Detective"
#define MyAppVersion   "0.1.0"
#define MyAppPublisher "GW Detective"
#define MyAppExeName   "GWDetective.exe"
#define MyAppId        "{{B8E2A2C0-9F7E-4B5D-9F2C-7E3E1A9B0001}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
DefaultDirName={localappdata}\Programs\GWDetective
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=Output
OutputBaseFilename=GWDetective-Setup-{#ARCH}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Icon shown in the setup .exe itself (Explorer + wizard title bar).
SetupIconFile=..\app.ico
; Silent upgrades launched by the in-app Updater need to overwrite a
; running GWDetective.exe — let Inno close it gracefully first.
CloseApplications=force
RestartApplications=no
#if ARCH == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#elif ARCH == "x64"
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
; Main app (single-file publish output for the chosen arch) + the web folder
; that ships alongside it.
Source: "..\bin\Release\net8.0-windows\win-{#ARCH}\publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\bin\Release\net8.0-windows\win-{#ARCH}\publish\web\*";           DestDir: "{app}\web"; Flags: ignoreversion recursesubdirs createallsubdirs

; Bundled WebView2 Evergreen Bootstrapper. Tiny (~1.7 MB) — it pulls the
; actual runtime down on demand when it elevates and runs.
Source: "third-party\MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
; IconFilename pins the shortcuts to the embedded exe icon so they look
; right even before WebView2 has spun the app up for the first time.
Name: "{group}\{#MyAppName}";                   Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}";         Filename: "{uninstallexe}"
Name: "{userdesktop}\{#MyAppName}";             Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Install WebView2 Runtime only if missing. Bootstrapper exits 0 even when
; the runtime is already present, so this is also safe as a no-op.
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; \
    Parameters: "/silent /install"; \
    StatusMsg: "Installing Microsoft Edge WebView2 Runtime..."; \
    Check: NeedsWebView2; \
    Flags: waituntilterminated

; Launch the app at the end of setup (user's choice via checkbox).
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; \
    Flags: nowait postinstall skipifsilent

; Silent relaunch path used by the in-app auto-updater. The updater spawns
; this installer with /VERYSILENT /LAUNCHAPP; postinstall+skipifsilent on
; the line above would otherwise suppress the launch.
Filename: "{app}\{#MyAppExeName}"; Flags: nowait; Check: WantsSilentLaunch

[Code]
// Returns True if no Edge WebView2 Runtime is registered on the machine.
// WebView2 publishes its presence under HKLM or HKCU EdgeUpdate Clients
// key with a non-empty 'pv' value.
function HasWebView2Pv(RootKey: Integer; SubKey: String): Boolean;
var
  Pv: String;
begin
  Result := False;
  if RegQueryStringValue(RootKey, SubKey, 'pv', Pv) then
    if (Pv <> '') and (Pv <> '0.0.0.0') then
      Result := True;
end;

function NeedsWebView2(): Boolean;
begin
  if HasWebView2Pv(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\' + '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}') then
    Result := False
  else if HasWebView2Pv(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\' + '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}') then
    Result := False
  else if HasWebView2Pv(HKCU, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\' + '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}') then
    Result := False
  else
    Result := True;
end;

// True when the installer was launched by the in-app updater with
// /LAUNCHAPP, meaning we should silently relaunch the app post-install.
function WantsSilentLaunch(): Boolean;
var
  i: Integer;
begin
  Result := False;
  for i := 1 to ParamCount do
    if CompareText(ParamStr(i), '/LAUNCHAPP') = 0 then
    begin
      Result := True;
      Exit;
    end;
end;
