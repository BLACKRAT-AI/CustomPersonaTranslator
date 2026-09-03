; CustomPersonaTranslator Inno Setup script.
;
; Produces a single .exe installer that:
;   - Installs the published self-contained WPF app to %LOCALAPPDATA%\Programs\CPT
;     (no admin required).
;   - Drops the HologramWeb assets, default persona, UIA adapter profiles, and
;     the Chatterbox helper script alongside the app.
;   - On install, optionally runs scripts\bootstrap.ps1 to fetch Piper, Whisper,
;     yt-dlp and ffmpeg (~500 MB download). Required for basic operation.
;   - On install, optionally runs scripts\bootstrap_chatterbox.ps1 to set up
;     voice cloning (~3 GB additional download once first used).
;   - Creates a Start Menu entry and an optional desktop shortcut.
;   - Detects Ollama on PATH and shows a hint if it is missing.
;
; Build with: pwsh -File installer\build.ps1

#define MyAppName        "CustomPersonaTranslator"
#define MyAppVersion     "0.4.0"
#define MyAppPublisher   "CustomPersonaTranslator"
#define MyAppExeName     "CPT.Shell.exe"
#define MyAppId          "{{B6B97C3D-9F84-4B7E-8A77-7E1B5C2DBF9F}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=dist
OutputBaseFilename=CPT-Setup-{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
UninstallDisplayName={#MyAppName}
SetupLogging=yes
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts"; Flags: unchecked
Name: "bootstrap";   Description: "Download required tools after install (Piper, Whisper, yt-dlp, ffmpeg — ~500 MB)"; GroupDescription: "Setup"; Flags: checkedonce
Name: "cloning";     Description: "Set up voice cloning (Chatterbox — ~3 GB extra). Recommended; uncheck only if you don't want voice clones."; GroupDescription: "Setup"; Flags: checkedonce
Name: "launch";      Description: "Launch {#MyAppName} after install"; GroupDescription: "Setup"; Flags: checkedonce

[Files]
; Published app: dotnet publish output for CPT.Shell.
Source: "build\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; Hologram assets (already copied into publish by csproj Content includes, but
; we include the source tree as a fallback so manual rebuilds still find them).
Source: "..\src\CPT.HologramWeb\*"; DestDir: "{app}\HologramWeb"; Flags: ignoreversion recursesubdirs createallsubdirs

; Bundled personas + their voice-sample / avatar assets. AppServices seeds
; %LOCALAPPDATA%\CustomPersonaTranslator\personas from {app}\personas on first
; run. Belt-and-suspenders fallback alongside the csproj Content includes.
Source: "..\personas\*.json";              DestDir: "{app}\personas";               Flags: ignoreversion
Source: "..\personas\voice-samples\*";     DestDir: "{app}\personas\voice-samples"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\personas\avatars\*";           DestDir: "{app}\personas\avatars";       Flags: ignoreversion recursesubdirs createallsubdirs

; Bootstrap scripts.
Source: "..\scripts\bootstrap.ps1";              DestDir: "{app}\scripts"; Flags: ignoreversion
Source: "..\scripts\bootstrap_chatterbox.ps1";   DestDir: "{app}\scripts"; Flags: ignoreversion

; Voice-cloning helper (small Python script the Chatterbox engine spawns).
Source: "..\tools\voiceclone\clone_server.py";   DestDir: "{app}\tools\voiceclone"; Flags: ignoreversion

; Browser + VS Code extensions are bundled for reference (developer-mode load).
Source: "..\extensions\browser\*";  DestDir: "{app}\extensions\browser";  Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\extensions\vscode\*";   DestDir: "{app}\extensions\vscode";   Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}";              Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Browser extension folder";  Filename: "{app}\extensions\browser"
Name: "{group}\Uninstall {#MyAppName}";    Filename: "{uninstallexe}"
Name: "{commondesktop}\{#MyAppName}";      Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Required tools (Piper / Whisper / yt-dlp / ffmpeg).
Filename: "powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\bootstrap.ps1"""; \
    WorkingDir: "{app}"; \
    Flags: postinstall waituntilterminated runhidden; \
    StatusMsg: "Downloading required tools (Piper, Whisper, yt-dlp, ffmpeg)…"; \
    Tasks: bootstrap

; Optional voice cloning (Chatterbox).
Filename: "powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\bootstrap_chatterbox.ps1"""; \
    WorkingDir: "{app}"; \
    Flags: postinstall waituntilterminated runhidden; \
    StatusMsg: "Setting up Chatterbox voice cloning (large download, GPU recommended)…"; \
    Tasks: cloning

; Auto-launch.
Filename: "{app}\{#MyAppExeName}"; \
    WorkingDir: "{app}"; \
    Flags: postinstall nowait skipifsilent; \
    Description: "Launch {#MyAppName}"; \
    Tasks: launch

[UninstallDelete]
; Remove HologramWeb cache, downloaded tools dir, and bundled persona assets
; from the app dir. User data in %LOCALAPPDATA%\CustomPersonaTranslator
; (settings.json + the user's actual persona library + voice samples that
; were imported via YouTube) is intentionally left intact.
Type: filesandordirs; Name: "{app}\tools"
Type: filesandordirs; Name: "{app}\HologramWeb"
Type: filesandordirs; Name: "{app}\personas"

[Code]
function InitializeSetup(): Boolean;
var
  WebView2: String;
  msg: String;
begin
  Result := True;
  // WebView2 Runtime check (HKLM 64-bit registry). On Windows 11 it is preinstalled,
  // but Win10 users may need to grab it.
  if not RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', WebView2) then
  begin
    msg := 'Microsoft Edge WebView2 Runtime was not detected.' + #13#10 +
           'It ships with Windows 11. On Windows 10 you may need to install it from' + #13#10 +
           'https://developer.microsoft.com/microsoft-edge/webview2/ before CPT will work.' + #13#10#13#10 +
           'Continue install anyway?';
    Result := MsgBox(msg, mbConfirmation, MB_YESNO) = IDYES;
  end;
end;

; First run opens the CLI setup screen, so a new user lands directly on the one
; step CPT cannot do for them: signing in to their coding CLI.
Filename: "{app}\{#MyAppExeName}"; \
    Parameters: "--setup"; \
    Description: "Connect a coding CLI"; \
    Flags: postinstall nowait skipifsilent
