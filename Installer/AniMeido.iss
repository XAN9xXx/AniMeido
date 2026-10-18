; ISCC /DAppVersion=3.0.0 /DInstallImage=<image> /DOutputDirectory=<output> AniMeido.iss
#ifndef AppVersion
    #error AppVersion must be supplied with /D
#endif
#ifndef InstallImage
    #error InstallImage must be supplied with /D
#endif
#ifndef OutputDirectory
    #error OutputDirectory must be supplied with /D
#endif

[Setup]
; Keep in sync with AniMeido.App/Services/InstallationRegistry.cs: InstallationIdentity.AppId.
AppId={{0978F867-ECE7-4110-9BEB-E976B7B8AC25}
AppName=AniMeido
AppVersion={#AppVersion}
AppPublisher=XAN9x
AppPublisherURL=https://animeido.com/
AppSupportURL=https://animeido.com/
AppUpdatesURL=https://animeido.com/
PrivilegesRequired=lowest
DefaultDirName={userpf}\AniMeido
AppendDefaultDirName=yes
DisableDirPage=no
UsePreviousAppDir=yes
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\AniMeido.exe
OutputDir={#OutputDirectory}
OutputBaseFilename=AniMeido_setup_{#AppVersion}
SetupIconFile=..\AniMeido.App\Assets\icon.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[CustomMessages]
english.Migration=An old AniMeido installation was detected. After installation, you will be asked to uninstall it. Tracking records and other personal data are stored in your user folders and will not be affected.
chinesesimplified.Migration=检测到旧版 AniMeido。新版本安装完成后会提示卸载旧版本；追番记录等数据保存在用户目录，不受影响。
english.ExitLegacy=Please fully exit old AniMeido, including its tray icon, before installing. Click Retry after exiting.
chinesesimplified.ExitLegacy=请完全退出旧版 AniMeido（包括托盘），再继续安装。退出后请点击“重试”。
english.ProcessCheckFailed=Unable to verify running AniMeido processes. Installation cannot continue safely. Fully exit AniMeido and retry.
chinesesimplified.ProcessCheckFailed=无法确认 AniMeido 进程是否已经退出，不能安全继续。请完全退出后重试。
english.Downgrade=An installed version is newer than this installer. Downgrading may make the database unreadable. Installation was refused.
chinesesimplified.Downgrade=已安装更高版本，降级可能导致数据库无法打开。已拒绝安装。
english.InvalidState=Unable to read a valid current version from state.json. Installation was refused to avoid an accidental downgrade.
chinesesimplified.InvalidState=无法从 state.json 读取有效的 current 版本。为避免意外降级，已拒绝安装。
english.Overlap=Choose a location separate from the old installation. Uninstalling the old version could otherwise remove the new version.
chinesesimplified.Overlap=请选择与旧安装互不包含的目录，否则卸载旧版本可能删除新版本。
english.UninstallLegacy=Uninstall the old AniMeido version now? Administrator confirmation may be required. Personal data will be retained.
chinesesimplified.UninstallLegacy=现在卸载旧版本？可能会弹出管理员确认。追番记录等个人数据将保留。
english.ManualUninstall=The old version was not uninstalled or the operation was cancelled. Please uninstall it later in Windows Installed apps. The new installation is unaffected.
chinesesimplified.ManualUninstall=旧版本未卸载或操作已取消。请以后在 Windows“已安装的应用”中手动卸载；新安装不受影响。
english.DeleteData=Also delete personal data (tracking records, settings and plugins)? This cannot be undone.
chinesesimplified.DeleteData=是否同时删除个人数据（追番记录、设置、插件）？此操作不可恢复。
english.DataDeleteFailed=Some personal data could not be deleted. Please check the AniMeido folders in AppData manually.
chinesesimplified.DataDeleteFailed=部分个人数据无法删除，请自行检查 AppData 中的 AniMeido 目录。
english.ExitNew=Please fully exit AniMeido and PluginHost, including the tray icon, before uninstalling. Click Retry after exiting.
chinesesimplified.ExitNew=卸载前请完全退出 AniMeido 和 PluginHost（包括托盘）。退出后请点击“重试”。

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#InstallImage}\AniMeido.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#InstallImage}\app-{#AppVersion}\*"; DestDir: "{app}\app-{#AppVersion}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Always replace the packaged initial state; previous and pending are null in this image.
Source: "{#InstallImage}\state.json"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\AniMeido"; Filename: "{app}\AniMeido.exe"; AppUserModelID: "AniMeido.Desktop"
Name: "{userdesktop}\AniMeido"; Filename: "{app}\AniMeido.exe"; AppUserModelID: "AniMeido.Desktop"; Tasks: desktopicon

[Run]
Filename: "{app}\AniMeido.exe"; Description: "{cm:LaunchProgram,AniMeido}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{app}\AniMeido.exe"
Type: filesandordirs; Name: "{app}\app-*"
Type: filesandordirs; Name: "{app}\*.staging"
Type: filesandordirs; Name: "{app}\packages"
Type: files; Name: "{app}\state.json"
Type: files; Name: "{app}\state.lock"
Type: files; Name: "{app}\update.lock"
Type: dirifempty; Name: "{app}"

[Code]
const
    LegacyKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{90DF1984-8380-4E35-B350-A269548C06E8}_is1';

var
    LegacyDirectories, LegacyCommands: TArrayOfString;
    DeletePersonalData: Boolean;
    LegacyDetectionFailed: Boolean;

function SplitParts(const Text: String): TArrayOfString;
var
    P, Start, N: Integer;
begin
    SetArrayLength(Result, 0);
    Start := 1;
    for P := 1 to Length(Text) + 1 do
        if (P > Length(Text)) or (Text[P] = '.') then
        begin
            N := GetArrayLength(Result);
            SetArrayLength(Result, N + 1);
            Result[N] := Copy(Text, Start, P - Start);
            Start := P + 1;
        end;
end;

function IsNumeric(const Value: String): Boolean;
var
    I: Integer;
begin
    Result := Length(Value) > 0;
    for I := 1 to Length(Value) do
        if (Value[I] < '0') or (Value[I] > '9') then
            Result := False;
end;

function ValidIdentifiers(const Value: String; const Prerelease: Boolean): Boolean;
var
    Parts: TArrayOfString;
    I, J: Integer;
    C: Char;
begin
    Result := False;
    Parts := SplitParts(Value);
    for I := 0 to GetArrayLength(Parts) - 1 do
    begin
        if Parts[I] = '' then Exit;
        for J := 1 to Length(Parts[I]) do
        begin
            C := Parts[I][J];
            if not (((C >= '0') and (C <= '9')) or ((C >= 'A') and (C <= 'Z')) or
                ((C >= 'a') and (C <= 'z')) or (C = '-')) then Exit;
        end;
        if Prerelease and IsNumeric(Parts[I]) and (Length(Parts[I]) > 1) and
            (Parts[I][1] = '0') then Exit;
    end;
    Result := GetArrayLength(Parts) > 0;
end;

function ParseVersion(Value: String; var Core, Pre: TArrayOfString): Boolean;
var
    P, I: Integer;
    Text: String;
begin
    Result := False;
    SetArrayLength(Pre, 0);
    P := Pos('+', Value);
    if P > 0 then
    begin
        Text := Copy(Value, P + 1, Length(Value));
        if not ValidIdentifiers(Text, False) then Exit;
        Value := Copy(Value, 1, P - 1);
    end;
    P := Pos('-', Value);
    if P > 0 then
    begin
        Text := Copy(Value, P + 1, Length(Value));
        if not ValidIdentifiers(Text, True) then Exit;
        Pre := SplitParts(Text);
        Value := Copy(Value, 1, P - 1);
    end;
    Core := SplitParts(Value);
    if GetArrayLength(Core) <> 3 then Exit;
    for I := 0 to 2 do
        if not IsNumeric(Core[I]) or ((Length(Core[I]) > 1) and (Core[I][1] = '0')) then Exit;
    Result := True;
end;

function CompareOrdinal(const Left, Right: String): Integer;
var
    I, Limit: Integer;
begin
    Limit := Length(Left);
    if Length(Right) < Limit then Limit := Length(Right);
    Result := 0;
    for I := 1 to Limit do
    begin
        Result := Ord(Left[I]) - Ord(Right[I]);
        if Result <> 0 then Exit;
    end;
    Result := Length(Left) - Length(Right);
end;

function CompareNumeric(const Left, Right: String): Integer;
begin
    Result := Length(Left) - Length(Right);
    if Result = 0 then Result := CompareOrdinal(Left, Right);
end;

function CompareVersions(const Left, Right: String; var Comparison: Integer): Boolean;
var
    LC, LP, RC, RP: TArrayOfString;
    I, Limit: Integer;
    LN, RN: Boolean;
begin
    Result := ParseVersion(Left, LC, LP) and ParseVersion(Right, RC, RP);
    if not Result then Exit;
    for I := 0 to 2 do
    begin
        Comparison := CompareNumeric(LC[I], RC[I]);
        if Comparison <> 0 then Exit;
    end;
    if (GetArrayLength(LP) = 0) or (GetArrayLength(RP) = 0) then
    begin
        Comparison := 0;
        if GetArrayLength(LP) <> GetArrayLength(RP) then
        begin
            if GetArrayLength(LP) = 0 then Comparison := 1 else Comparison := -1;
        end;
        Exit;
    end;
    Limit := GetArrayLength(LP);
    if GetArrayLength(RP) < Limit then Limit := GetArrayLength(RP);
    for I := 0 to Limit - 1 do
    begin
        LN := IsNumeric(LP[I]);
        RN := IsNumeric(RP[I]);
        if LN and RN then Comparison := CompareNumeric(LP[I], RP[I])
        else if LN <> RN then
        begin
            if LN then Comparison := -1 else Comparison := 1;
        end
        else Comparison := CompareOrdinal(LP[I], RP[I]);
        if Comparison <> 0 then Exit;
    end;
    Comparison := GetArrayLength(LP) - GetArrayLength(RP);
end;

procedure SkipWhitespace(const Text: String; var P: Integer);
begin
    while P <= Length(Text) do
    begin
        if (Text[P] <> ' ') and (Text[P] <> #9) and (Text[P] <> #10) and (Text[P] <> #13) then Exit;
        P := P + 1;
    end;
end;

function ReadJsonString(const Text: String; var P: Integer; var Value: String): Boolean;
var
    C: Char;
    I, Code, Digit: Integer;
begin
    Result := False;
    Value := '';
    if (P > Length(Text)) or (Text[P] <> '"') then Exit;
    P := P + 1;
    while P <= Length(Text) do
    begin
        C := Text[P];
        P := P + 1;
        if C = '"' then
        begin
            Result := True;
            Exit;
        end;
        if Ord(C) < 32 then Exit;
        if C = '\' then
        begin
            if P > Length(Text) then Exit;
            C := Text[P];
            P := P + 1;
            case C of
                '"', '\', '/': ;
                'b': C := #8;
                'f': C := #12;
                'n': C := #10;
                'r': C := #13;
                't': C := #9;
                'u':
                begin
                    Code := 0;
                    for I := 1 to 4 do
                    begin
                        if P > Length(Text) then Exit;
                        Digit := Pos(Uppercase(Text[P]), '0123456789ABCDEF') - 1;
                        if Digit < 0 then Exit;
                        Code := Code * 16 + Digit;
                        P := P + 1;
                    end;
                    C := Chr(Code);
                end;
            else
                Exit;
            end;
        end;
        Value := Value + C;
    end;
end;

function ValidJsonNumber(const Value: String): Boolean;
var
    P, Start: Integer;
begin
    Result := False;
    P := 1;
    if Copy(Value, P, 1) = '-' then P := P + 1;
    Start := P;
    while (P <= Length(Value)) and (Value[P] >= '0') and (Value[P] <= '9') do P := P + 1;
    if (P = Start) or ((P - Start > 1) and (Value[Start] = '0')) then Exit;
    if Copy(Value, P, 1) = '.' then
    begin
        P := P + 1;
        Start := P;
        while (P <= Length(Value)) and (Value[P] >= '0') and (Value[P] <= '9') do P := P + 1;
        if P = Start then Exit;
    end;
    if (Copy(Value, P, 1) = 'e') or (Copy(Value, P, 1) = 'E') then
    begin
        P := P + 1;
        if (Copy(Value, P, 1) = '+') or (Copy(Value, P, 1) = '-') then P := P + 1;
        Start := P;
        while (P <= Length(Value)) and (Value[P] >= '0') and (Value[P] <= '9') do P := P + 1;
        if P = Start then Exit;
    end;
    Result := P = Length(Value) + 1;
end;

// Parse the whole JSON, capturing only the root object's current string, never nested current.
function ReadJsonValue(const Text: String; var P: Integer; const Depth: Integer;
    var Current: String; var Found: Boolean): Boolean;
var
    Opening, Closing: Char;
    Key, Value: String;
    Start: Integer;
begin
    Result := False;
    if Depth > 64 then Exit;
    SkipWhitespace(Text, P);
    if P > Length(Text) then Exit;
    Opening := Text[P];
    if Opening = '"' then
    begin
        Result := ReadJsonString(Text, P, Value);
        Exit;
    end;
    if (Opening = '{') or (Opening = '[') then
    begin
        if Opening = '{' then Closing := '}' else Closing := ']';
        P := P + 1;
        SkipWhitespace(Text, P);
        if Copy(Text, P, 1) = Closing then
        begin
            P := P + 1;
            Result := True;
            Exit;
        end;
        while P <= Length(Text) do
        begin
            if Opening = '{' then
            begin
                if not ReadJsonString(Text, P, Key) then Exit;
                SkipWhitespace(Text, P);
                if Copy(Text, P, 1) <> ':' then Exit;
                P := P + 1;
                SkipWhitespace(Text, P);
            end;
            if (Depth = 0) and (Opening = '{') and (Key = 'current') then
            begin
                if Found or not ReadJsonString(Text, P, Current) then Exit;
                Found := True;
            end
            else if not ReadJsonValue(Text, P, Depth + 1, Current, Found) then Exit;
            SkipWhitespace(Text, P);
            if Copy(Text, P, 1) = Closing then
            begin
                P := P + 1;
                Result := True;
                Exit;
            end;
            if Copy(Text, P, 1) <> ',' then Exit;
            P := P + 1;
            SkipWhitespace(Text, P);
        end;
        Exit;
    end;
    Start := P;
    while P <= Length(Text) do
    begin
        if Pos(Text[P], ',]} ' + #9 + #10 + #13) > 0 then Break;
        P := P + 1;
    end;
    Value := Copy(Text, Start, P - Start);
    Result := (Value = 'true') or (Value = 'false') or (Value = 'null') or ValidJsonNumber(Value);
end;

function ReadCurrent(const Path: String; var Current: String): Boolean;
var
    Bytes: AnsiString;
    Text: String;
    P: Integer;
    Found: Boolean;
begin
    Result := False;
    if not LoadStringFromFile(Path, Bytes) then Exit;
    if Length(Bytes) > 65536 then Exit;
    Text := UTF8Decode(Bytes);
    if Copy(Text, 1, 1) = #$FEFF then Delete(Text, 1, 1);
    P := 1;
    Found := False;
    SkipWhitespace(Text, P);
    if Copy(Text, P, 1) <> '{' then Exit;
    if not ReadJsonValue(Text, P, 0, Current, Found) then Exit;
    SkipWhitespace(Text, P);
    Result := Found and (P > Length(Text));
end;

procedure ReadLegacy(const Root: Integer);
var
    Directory, Command: String;
    N: Integer;
begin
    if not RegKeyExists(Root, LegacyKey) then Exit;
    if not RegQueryStringValue(Root, LegacyKey, 'InstallLocation', Directory) then
        Log('Warning: legacy InstallLocation is unavailable.');
    if not RegQueryStringValue(Root, LegacyKey, 'UninstallString', Command) then
        Log('Warning: legacy UninstallString is unavailable.');
    N := GetArrayLength(LegacyCommands);
    SetArrayLength(LegacyDirectories, N + 1);
    SetArrayLength(LegacyCommands, N + 1);
    LegacyDirectories[N] := Directory;
    LegacyCommands[N] := Command;
end;

procedure DetectLegacy();
begin
    LegacyDetectionFailed := False;
    SetArrayLength(LegacyDirectories, 0);
    SetArrayLength(LegacyCommands, 0);
    try
        ReadLegacy(HKLM64);
        ReadLegacy(HKCU64);
    except
        Log('Warning: legacy detection failed: ' + GetExceptionMessage);
        LegacyDetectionFailed := True;
    end;
end;

function InDirectory(const Path, Directory: String): Boolean;
var
    FullPath, Prefix: String;
begin
    Result := False;
    if Directory = '' then Exit;
    FullPath := Lowercase(ExpandFileName(Path));
    Prefix := Lowercase(AddBackslash(ExpandFileName(Directory)));
    Result := (FullPath = Copy(Prefix, 1, Length(Prefix) - 1)) or (Pos(Prefix, FullPath) = 1);
end;

// Return 0 = stopped, 1 = running, 2 = cannot verify. Never rely on process name alone.
function RunningInDirectories(const Directories: TArrayOfString; const IncludeHost: Boolean): Integer;
var
    WMI, Processes, Process, PathValue: Variant;
    Query, Path: String;
    I, J: Integer;
begin
    Result := 0;
    if GetArrayLength(Directories) = 0 then Exit;
    try
        WMI := CreateOleObject('WbemScripting.SWbemLocator');
        WMI := WMI.ConnectServer('', 'root\CIMV2');
        Query := 'SELECT ExecutablePath FROM Win32_Process WHERE Name = ''AniMeido.App.exe''';
        if IncludeHost then Query := Query + ' OR Name = ''AniMeido.PluginHost.exe''';
        Processes := WMI.ExecQuery(Query);
        for I := 0 to Processes.Count - 1 do
        begin
            Process := Processes.ItemIndex(I);
            PathValue := Process.ExecutablePath;
            if VarIsNull(PathValue) or VarIsEmpty(PathValue) then
            begin
                Log('Warning: a running AniMeido process has no readable executable path.');
                Result := 2;
                Exit;
            end;
            Path := PathValue;
            for J := 0 to GetArrayLength(Directories) - 1 do
            begin
                if Directories[J] = '' then
                begin
                    Log('Warning: legacy installation directory is unavailable.');
                    Result := 2;
                    Exit;
                end;
                if InDirectory(Path, Directories[J]) then
                begin
                    Result := 1;
                    Exit;
                end;
            end;
        end;
    except
        Log('Warning: process detection failed: ' + GetExceptionMessage);
        Result := 2;
    end;
end;

procedure InitializeWizard();
begin
    DetectLegacy();
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
    MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
    Result := MemoDirInfo + NewLine + MemoGroupInfo + NewLine + MemoTasksInfo;
    if GetArrayLength(LegacyCommands) > 0 then
        Result := Result + NewLine + NewLine + CustomMessage('Migration');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
    Path, Current: String;
    Comparison, Running, I: Integer;
begin
    Result := '';
    DetectLegacy();
    if LegacyDetectionFailed then
    begin
        Result := CustomMessage('ProcessCheckFailed');
        Exit;
    end;
    for I := 0 to GetArrayLength(LegacyDirectories) - 1 do
        if (LegacyDirectories[I] <> '') and
            (InDirectory(ExpandConstant('{app}'), LegacyDirectories[I]) or
            InDirectory(LegacyDirectories[I], ExpandConstant('{app}'))) then
        begin
            Result := CustomMessage('Overlap');
            Exit;
        end;
    Path := ExpandConstant('{app}\state.json');
    if FileExists(Path) then
    begin
        if not ReadCurrent(Path, Current) or not CompareVersions('{#AppVersion}', Current, Comparison) then
        begin
            Log('Warning: refusing overwrite because state.json current is invalid.');
            Result := CustomMessage('InvalidState');
            Exit;
        end;
        if Comparison < 0 then
        begin
            Result := CustomMessage('Downgrade');
            Exit;
        end;
    end;
    Running := RunningInDirectories(LegacyDirectories, False);
    if Running = 1 then Result := CustomMessage('ExitLegacy')
    else if Running = 2 then Result := CustomMessage('ProcessCheckFailed');
    // A nonempty result keeps Inno on Preparing to Install with Retry; no files are written.
end;

procedure RegisterExtraCloseApplicationsResources();
var
    Find: TFindRec;
    Directory: String;
begin
    // Include versions not present in this installer, so Restart Manager sees current/previous.
    if FindFirst(ExpandConstant('{app}\app-*'), Find) then
    try
        repeat
            if Find.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0 then
            begin
                Directory := ExpandConstant('{app}\') + Find.Name;
                RegisterExtraCloseApplicationsResource(False, Directory + '\AniMeido.App.exe');
                RegisterExtraCloseApplicationsResource(False, Directory + '\PluginHost\AniMeido.PluginHost.exe');
            end;
        until not FindNext(Find);
    finally
        FindClose(Find);
    end;
end;

function SplitCommand(Command: String; var FileName, Arguments: String): Boolean;
var
    P: Integer;
begin
    Result := False;
    Command := Trim(Command);
    if Copy(Command, 1, 1) = '"' then
    begin
        Delete(Command, 1, 1);
        P := Pos('"', Command);
        if P = 0 then Exit;
        FileName := Copy(Command, 1, P - 1);
        Arguments := Trim(Copy(Command, P + 1, Length(Command)));
    end
    else
    begin
        P := Pos('.exe', Lowercase(Command));
        if P = 0 then Exit;
        FileName := Copy(Command, 1, P + 3);
        Arguments := Trim(Copy(Command, P + 4, Length(Command)));
    end;
    Result := (Lowercase(ExtractFileExt(FileName)) = '.exe') and
        (ExtractFileDrive(FileName) <> '');
end;

procedure PreserveNewShortcut(const Shortcut, Temporary: String);
begin
    try
        // A legacy HKCU install may own the same link. Copy the new link with its AppUserModelID.
        if not FileCopy(Shortcut, Temporary, False) then
            Log('Warning: unable to preserve new shortcut: ' + Shortcut);
    except
        Log('Warning: preserving new shortcut failed: ' + GetExceptionMessage);
    end;
end;

procedure RestoreNewShortcut(const Temporary, Shortcut: String);
begin
    try
        if FileExists(Temporary) then
            if not FileExists(Shortcut) then
                if not FileCopy(Temporary, Shortcut, False) then
                    Log('Warning: unable to restore new shortcut: ' + Shortcut);
    except
        Log('Warning: restoring new shortcut failed: ' + GetExceptionMessage);
    end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
    I, ExitCode: Integer;
    FileName, Arguments, MenuShortcut, DesktopShortcut, SavedMenu, SavedDesktop: String;
    Success: Boolean;
begin
    if (CurStep <> ssPostInstall) or (GetArrayLength(LegacyCommands) = 0) then Exit;
    if SuppressibleMsgBox(CustomMessage('UninstallLegacy'), mbConfirmation, MB_YESNO, IDYES) <> IDYES then
    begin
        SuppressibleMsgBox(CustomMessage('ManualUninstall'), mbInformation, MB_OK, IDOK);
        Exit;
    end;
    MenuShortcut := ExpandConstant('{userprograms}\AniMeido.lnk');
    DesktopShortcut := ExpandConstant('{userdesktop}\AniMeido.lnk');
    SavedMenu := ExpandConstant('{tmp}\AniMeido-new-menu.lnk');
    SavedDesktop := ExpandConstant('{tmp}\AniMeido-new-desktop.lnk');
    PreserveNewShortcut(MenuShortcut, SavedMenu);
    if WizardIsTaskSelected('desktopicon') then
        PreserveNewShortcut(DesktopShortcut, SavedDesktop);
    for I := 0 to GetArrayLength(LegacyCommands) - 1 do
    begin
        Success := False;
        try
            if SplitCommand(LegacyCommands[I], FileName, Arguments) then
                Success := ShellExec('runas', FileName, Arguments, '', SW_SHOWNORMAL, ewWaitUntilTerminated, ExitCode);
            if Success then Success := ExitCode = 0;
        except
            Log('Warning: legacy uninstall failed: ' + GetExceptionMessage);
        end;
        if not Success then
        begin
            Log('Warning: legacy uninstall failed or was cancelled.');
            SuppressibleMsgBox(CustomMessage('ManualUninstall'), mbInformation, MB_OK, IDOK);
        end;
    end;
    RestoreNewShortcut(SavedMenu, MenuShortcut);
    if WizardIsTaskSelected('desktopicon') then
        RestoreNewShortcut(SavedDesktop, DesktopShortcut);
    // Inno removes its own temporary directory; no permanent recovery artifacts are created.
end;

function InitializeSetup(): Boolean;
var
    Core, Pre: TArrayOfString;
begin
    Result := ParseVersion('{#AppVersion}', Core, Pre);
    if not Result then
        MsgBox('Invalid installer SemVer.', mbError, MB_OK);
end;

function InitializeUninstall(): Boolean;
var
    Directories: TArrayOfString;
    Running: Integer;
    Message: String;
begin
    Result := False;
    SetArrayLength(Directories, 1);
    Directories[0] := ExpandConstant('{app}');
    repeat
        Running := RunningInDirectories(Directories, True);
        if Running = 0 then Break;
        if Running = 1 then Message := CustomMessage('ExitNew')
        else Message := CustomMessage('ProcessCheckFailed');
        if SuppressibleMsgBox(Message, mbError, MB_RETRYCANCEL, IDCANCEL) <> IDRETRY then Exit;
    until False;
    DeletePersonalData := SuppressibleMsgBox(CustomMessage('DeleteData'), mbConfirmation,
        MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES;
    Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
    RoamingDeleted, LocalDeleted: Boolean;
begin
    if (CurUninstallStep <> usPostUninstall) or not DeletePersonalData then Exit;
    // A data directory that never existed counts as deleted; only real failures are reported.
    RoamingDeleted := (not DirExists(ExpandConstant('{userappdata}\AniMeido'))) or
        DelTree(ExpandConstant('{userappdata}\AniMeido'), True, True, True);
    LocalDeleted := (not DirExists(ExpandConstant('{localappdata}\AniMeido'))) or
        DelTree(ExpandConstant('{localappdata}\AniMeido'), True, True, True);
    if not RoamingDeleted or not LocalDeleted then
    begin
        Log('Warning: personal data deletion was incomplete.');
        SuppressibleMsgBox(CustomMessage('DataDeleteFailed'), mbInformation, MB_OK, IDOK);
    end;
end;
