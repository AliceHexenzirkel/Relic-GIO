; Inno Setup script for Relic. Build the app first with build\publish.ps1, then compile:
;   "C:\Program Files (x86)\Inno Setup 6\iscc.exe" installer\relic.iss
; Produces installer\Output\RelicSetup.exe — a per-user installer (no admin needed).
;
; THIS FILE MUST STAY UTF-8 WITH BOM — and so must every lang\*.iss it includes. Inno reads a
; BOM-less script as system-codepage ANSI, which turns every non-ASCII character (the em dashes
; here, the Romanian diacritics in lang\ro.iss) into mojibake in the real wizard.
;
; Every user-visible string lives in lang\en.iss as a [CustomMessages] entry (referenced with
; {cm:Name} in sections and CustomMessage('Name') in [Code]); lang\ro.iss carries the original
; Romanian texts under the same names and is NOT included by default — see its header.

#define AppName "Relic"
#define AppVersion "1.0.0"
#define AppExe "Relic.exe"
; The installer file name: RelicSetup.exe, or whatever /DOutputName=<name> passes on the iscc command
; line (the build with the navmesh bundle inside is compiled as RelicSetup-navmesh).
#ifndef OutputName
#define OutputName "RelicSetup"
#endif

[Setup]
AppId={{B7A6F5E2-9C41-4E7A-9F2D-RELIC0000001}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Relic
DefaultDirName={localappdata}\Programs\Relic
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename={#OutputName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Read from the source tree, not from publish\ — the .ico is committed and always present, so the
; installer compiles even before a publish run.
SetupIconFile=..\app\Relic.App\assets\relic.ico
UninstallDisplayIcon={app}\{#AppExe}
; Program.cs creates this mutex for the main instance (not for --play / --uninstall-cleanup, which
; return before it). Lets Setup ask the user to close a running Relic instead of failing to replace
; a locked Relic.exe — the reinstall-over-a-running-tray case.
AppMutex=Local\Relic.Launcher

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

; All wizard/uninstall strings. To add Romanian, see the header of lang\ro.iss.
#include "lang\en.iss"

[Tasks]
Name: "desktopicon"; Description: "{cm:TaskDesktopIcon}"; GroupDescription: "{cm:TaskGroupShortcuts}"

[InstallDelete]
; The agent folder is replaced whole. Its content IS "the agent this launcher ships" (the build identity
; is worked out over it), so a file an earlier Relic put there must not stay beside the new ones.
Type: filesandordirs; Name: "{app}\agent"

[Files]
; The self-contained app (publish\win-x64) minus debug/doc leftovers.
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Excludes: "*.pdb,*.xml,*.bak"; Flags: recursesubdirs createallsubdirs ignoreversion
; WebView2 Evergreen bootstrapper (installed only if the runtime is missing).
Source: "redist\MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
; Point at the EXE, not a loose .ico: <ApplicationIcon> embeds the icon as the exe's Win32 resource,
; and a single-file publish does not leave assets\relic.ico beside it. Inno validates IconFilename
; neither at compile nor at install time, so a stale path here would silently give blank shortcuts.
; AppUserModelID: the same identity the process declares for itself at startup (Interop.
; DeclareAppIdentity, "Relic.Launcher"). The process MUST declare it — otherwise the taskbar button
; borrows the icon of the shortcut that launched it, and the game shortcut carries the Genshin icon.
; Once declared there, it has to be written here too: a button pinned to the taskbar binds to the
; running window only if the two identities match, otherwise two buttons appear for the same
; application. Keep them identical.
Name: "{group}\Relic"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppExe}"; AppUserModelID: "Relic.Launcher"
Name: "{userdesktop}\Relic"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppExe}"; AppUserModelID: "Relic.Launcher"; Tasks: desktopicon

[Run]
; Ensure the WebView2 runtime is present (silent, no-op if already installed).
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; \
  Check: WebView2Missing; StatusMsg: "{cm:StatusInstallingWebView2}"; Flags: waituntilterminated
Filename: "{app}\{#AppExe}"; Description: "{cm:RunLaunchRelic}"; Flags: nowait postinstall skipifsilent

; Backstop for everything Relic writes outside {app}. Relic.exe --uninstall-cleanup normally removes
; the whole folder itself (it also has to restore the official Genshin profile first); these entries
; only matter when that could not run at all — a corrupted exe, a missing WebView2, an old build.
; NOTE the deliberate omission of "profiles": while a version's profile is loaded, that folder holds
; the ONLY copy of the official client's account data, and only the exe can tell whether it is safe
; to delete. A blind DelTree here would destroy a real Genshin account's local data.
[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\Relic\logs"
Type: filesandordirs; Name: "{localappdata}\Relic\WebView2"
Type: filesandordirs; Name: "{localappdata}\Relic\config"
Type: filesandordirs; Name: "{localappdata}\Relic\secrets"
Type: files;          Name: "{localappdata}\Relic\state.json"
Type: files;          Name: "{localappdata}\Relic\state.json.bak"
Type: files;          Name: "{localappdata}\Relic\state.json.tmp"
Type: files;          Name: "{localappdata}\Relic\state.json.corrupt"
Type: dirifempty;     Name: "{localappdata}\Relic"

[Code]
function WebView2Missing: Boolean;
var
  pv: string;
begin
  // Evergreen runtime client GUID; present under HKLM (system-wide) or HKCU (per-user).
  Result := True;
  if RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', pv) and (pv <> '') and (pv <> '0.0.0.0') then
    Result := False
  else if RegQueryStringValue(HKCU, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', pv) and (pv <> '') and (pv <> '0.0.0.0') then
    Result := False;
end;

// ─────────────────────────── uninstall ───────────────────────────
// Inno only owns {app}. Everything else — %LOCALAPPDATA%\Relic (state, isolated profiles, logs,
// WebView2), the per-version shortcuts, the autostart entry, the Fiddler script and certificate,
// the downloaded games — is removed by Relic.exe --uninstall-cleanup, the only party that knows the
// safety rules (before anything else: restore the official Genshin profile). Here we only ask what
// it should take along and report what was left behind.

var
  RemoveGames: Boolean;
  RemoveFiddler: Boolean;
  ProbeGamesText: String;
  ProbeGameCount: Integer;
  ProbeFiddlerInstalled: Boolean;

procedure LoadProbe;
var
  ProbeFile: String;
  Lines: TArrayOfString;
  I, P, Code: Integer;
  Key, Value: String;
begin
  ProbeGamesText := '';
  ProbeGameCount := 0;
  ProbeFiddlerInstalled := False;

  ProbeFile := ExpandConstant('{tmp}\relic-probe.txt');
  // A build older than this switch exits nonzero / writes nothing — the dialog then falls back to
  // the generic wording instead of claiming "0 versions".
  if not Exec(ExpandConstant('{app}\{#AppExe}'), '--uninstall-probe "' + ProbeFile + '"',
              '', SW_HIDE, ewWaitUntilTerminated, Code) then
    exit;
  if not LoadStringsFromFile(ProbeFile, Lines) then
    exit;

  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    P := Pos('=', Lines[I]);
    if P <= 0 then continue;
    Key := Trim(Copy(Lines[I], 1, P - 1));
    Value := Trim(Copy(Lines[I], P + 1, Length(Lines[I]) - P));
    if Key = 'gamesText' then ProbeGamesText := Value
    else if Key = 'gameCount' then ProbeGameCount := StrToIntDef(Value, 0)
    else if Key = 'fiddler' then ProbeFiddlerInstalled := (Value = '1');
  end;
end;

function AskUninstallOptions: Boolean;
var
  Form: TSetupForm;
  Intro, GamesHint, FiddlerHint: TNewStaticText;
  GamesChk, FiddlerChk: TNewCheckBox;
  OkBtn, CancelBtn: TNewButton;
  GamesCaption: String;
  W, Y: Integer;
begin
  // Fixed height: nothing in here grows vertically.
  Form := CreateCustomForm(ScaleX(452), ScaleY(268), False, True);
  try
    Form.Caption := CustomMessage('UninstCaption');

    Intro := TNewStaticText.Create(Form);
    Intro.Parent := Form;
    Intro.Left := ScaleX(16);
    Intro.Top := ScaleY(14);
    Intro.Width := Form.ClientWidth - ScaleX(32);
    Intro.Height := ScaleY(48);
    Intro.AutoSize := False;
    Intro.WordWrap := True;
    Intro.Caption := CustomMessage('UninstIntro');

    Y := ScaleY(72);

    if ProbeGameCount > 0 then
      GamesCaption := FmtMessage(CustomMessage('UninstGamesWithCount'), [IntToStr(ProbeGameCount), ProbeGamesText])
    else
      GamesCaption := CustomMessage('UninstGames');

    GamesChk := TNewCheckBox.Create(Form);
    GamesChk.Parent := Form;
    GamesChk.Left := ScaleX(16);
    GamesChk.Top := Y;
    GamesChk.Width := Form.ClientWidth - ScaleX(32);
    GamesChk.Height := ScaleY(18);
    GamesChk.Caption := GamesCaption;
    // UNCHECKED by default: it is tens of GB and a re-download of several hours.
    GamesChk.Checked := False;
    GamesChk.Enabled := (ProbeGameCount > 0);

    GamesHint := TNewStaticText.Create(Form);
    GamesHint.Parent := Form;
    GamesHint.Left := ScaleX(34);
    GamesHint.Top := Y + ScaleY(21);
    GamesHint.Width := Form.ClientWidth - ScaleX(50);
    GamesHint.Height := ScaleY(30);
    GamesHint.AutoSize := False;
    GamesHint.WordWrap := True;
    if ProbeGameCount > 0 then
      GamesHint.Caption := CustomMessage('UninstGamesHint')
    else
      GamesHint.Caption := CustomMessage('UninstGamesNone');

    Y := Y + ScaleY(60);

    FiddlerChk := TNewCheckBox.Create(Form);
    FiddlerChk.Parent := Form;
    FiddlerChk.Left := ScaleX(16);
    FiddlerChk.Top := Y;
    FiddlerChk.Width := Form.ClientWidth - ScaleX(32);
    FiddlerChk.Height := ScaleY(18);
    FiddlerChk.Caption := CustomMessage('UninstFiddler');
    FiddlerChk.Checked := ProbeFiddlerInstalled;
    FiddlerChk.Enabled := ProbeFiddlerInstalled;

    FiddlerHint := TNewStaticText.Create(Form);
    FiddlerHint.Parent := Form;
    FiddlerHint.Left := ScaleX(34);
    FiddlerHint.Top := Y + ScaleY(21);
    FiddlerHint.Width := Form.ClientWidth - ScaleX(50);
    FiddlerHint.Height := ScaleY(44);
    FiddlerHint.AutoSize := False;
    FiddlerHint.WordWrap := True;
    if ProbeFiddlerInstalled then
      FiddlerHint.Caption := CustomMessage('UninstFiddlerHint')
    else
      FiddlerHint.Caption := CustomMessage('UninstFiddlerNone');

    CancelBtn := TNewButton.Create(Form);
    CancelBtn.Parent := Form;
    CancelBtn.Caption := CustomMessage('UninstBtnCancel');
    CancelBtn.Height := ScaleY(25);
    CancelBtn.ModalResult := mrCancel;
    CancelBtn.Cancel := True;

    OkBtn := TNewButton.Create(Form);
    OkBtn.Parent := Form;
    OkBtn.Caption := CustomMessage('UninstBtnOk');
    OkBtn.Height := ScaleY(25);
    OkBtn.ModalResult := mrOk;
    OkBtn.Default := True;

    W := Form.CalculateButtonWidth([OkBtn.Caption, CancelBtn.Caption]);
    OkBtn.Width := W;
    CancelBtn.Width := W;
    CancelBtn.Left := Form.ClientWidth - ScaleX(16) - W;
    CancelBtn.Top := Form.ClientHeight - ScaleY(16) - CancelBtn.Height;
    OkBtn.Left := CancelBtn.Left - ScaleX(8) - W;
    OkBtn.Top := CancelBtn.Top;

    Form.ActiveControl := OkBtn;
    Result := (Form.ShowModal() = mrOk);
    if Result then
    begin
      RemoveGames := GamesChk.Checked and GamesChk.Enabled;
      RemoveFiddler := FiddlerChk.Checked and FiddlerChk.Enabled;
    end;
  finally
    Form.Free();
  end;
end;

function InitializeUninstall: Boolean;
begin
  RemoveGames := False;
  RemoveFiddler := False;
  if UninstallSilent then
  begin
    // Silent mode cannot ask: take the conservative decision (the games stay), but still clean up
    // the configuration and remove the certificate from the trusted list.
    RemoveFiddler := True;
    Result := True;
    exit;
  end;
  LoadProbe;
  Result := AskUninstallOptions;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Params: String;
  Code: Integer;
begin
  if CurUninstallStep <> usUninstall then
    exit;

  // Here, not in [UninstallRun]: we run BEFORE Inno deletes {app}\Relic.exe, and we need the exit
  // code to report what was left behind.
  Params := '--uninstall-cleanup';
  if RemoveGames then Params := Params + ' --games';
  if not RemoveFiddler then Params := Params + ' --keep-fiddler';

  if not Exec(ExpandConstant('{app}\{#AppExe}'), Params, '', SW_HIDE, ewWaitUntilTerminated, Code) then
  begin
    // Could not start at all — [UninstallDelete] above remains the only cleanup.
    if not UninstallSilent then
      MsgBox(CustomMessage('UninstCleanupNotRun'), mbInformation, MB_OK);
    exit;
  end;

  if (Code <> 0) and (not UninstallSilent) then
    // Keep the argument list on this line: a [Code] line that STARTS with "[" is read by the
    // compiler as a section tag and aborts the compile ("Invalid section tag").
    MsgBox(FmtMessage(CustomMessage('UninstLeftovers'), [ExpandConstant('{%TEMP}') + '\relic-uninstall.log']),
           mbInformation, MB_OK);
end;
