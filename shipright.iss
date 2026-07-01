; ShipRight InnoSetup installer
; Single command: iscc.exe shipright.iss
; Builds frontend + server + desktop from source, then packages into setup.exe

#define AppName "ShipRight"
#define AppVersion "2.7.0"
#define AppPublisher "ShipRight"
#define OutputDir "installer"
#define S SourcePath

; ---- Pre-build steps (ISPP Exec runs during preprocessing, before compilation) ----

; Clean previous publish output
#define CleanPublishResult = Exec("cmd.exe", "/c if exist """ + S + "\publish\win-x64"" rd /s /q """ + S + "\publish\win-x64""", "", 0, 1)

; 1. Generate icon files
#define IconResult = Exec("cmd.exe", "/c cd /d """ + S + "\front-end"" && npm run generate-icon", "", 1, 1)
#if IconResult != 0
  #error "Icon generation failed (exit code " + Str(IconResult) + ")"
#endif

; 2. Build Next.js frontend (outputs to front-end/out/)
#define FrontendResult = Exec("cmd.exe", "/c cd /d """ + S + "\front-end"" && npm run build", "", 1, 1)
#if FrontendResult != 0
  #error "Frontend build failed (exit code " + Str(FrontendResult) + ")"
#endif

; 3. Clean and copy wwwroot from front-end/out/ to back-end/ShipRight.Server/wwwroot/
#define CleanWwwResult = Exec("cmd.exe", "/c if exist """ + S + "\back-end\ShipRight.Server\wwwroot"" rd /s /q """ + S + "\back-end\ShipRight.Server\wwwroot""", "", 0, 1)
#define CopyWwwResult = Exec("cmd.exe", "/c robocopy """ + S + "\front-end\out"" """ + S + "\back-end\ShipRight.Server\wwwroot"" /e /nfl /ndl & exit 0", "", 0, 1)

; 4. Publish ShipRight.Server as single-file self-contained exe
#define PublishServerResult = Exec("dotnet", "publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true """ + S + "\back-end\ShipRight.Server\ShipRight.Server.csproj"" -o """ + S + "\publish\win-x64""", "", 1, 1)
#if PublishServerResult != 0
  #error "Server publish failed (exit code " + Str(PublishServerResult) + ")"
#endif

; 5. Publish ShipRight.Desktop (not self-contained — requires .NET runtime or framework-dependent)
#define PublishDesktopResult = Exec("dotnet", "publish -c Release -r win-x64 """ + S + "\back-end\ShipRight.Desktop\ShipRight.Desktop.csproj"" -o """ + S + "\publish\win-x64""", "", 1, 1)
#if PublishDesktopResult != 0
  #error "Desktop publish failed (exit code " + Str(PublishDesktopResult) + ")"
#endif

; ---- Compilation phase ----

[Setup]
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\ShipRight
DefaultGroupName=ShipRight
OutputDir={#OutputDir}
OutputBaseFilename=ShipRight-Setup-{#AppVersion}-win-x64
SetupIconFile=back-end\ShipRight.Server\shipright.ico
UninstallDisplayIcon={app}\ShipRight.Desktop.exe
Compression=lzma2
SolidCompression=yes
DisableProgramGroupPage=yes

[Components]
Name: "server";  Description: "ShipRight Server";  Types: full compact
Name: "desktop"; Description: "ShipRight Desktop UI"; Types: full compact

[Files]
; Server files
Source: "publish\win-x64\ShipRight.Server.exe"; DestDir: "{app}"; Flags: ignoreversion; Components: server
; Desktop files (includes server exe so standalone desktop works)
Source: "publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs; Components: desktop
Source: "back-end\ShipRight.Server\shipright.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\ShipRight"; Filename: "{app}\ShipRight.Desktop.exe"; WorkingDir: "{app}"; Components: desktop
Name: "{autodesktop}\ShipRight"; Filename: "{app}\ShipRight.Desktop.exe"; WorkingDir: "{app}"; Components: desktop
Name: "{autoprograms}\ShipRight (Headless Server)"; Filename: "{app}\ShipRight.Server.exe"; WorkingDir: "{app}"; Components: server; Flags: createonlyiffileexists

[Run]
Filename: "{app}\ShipRight.Desktop.exe"; Description: "Launch ShipRight Desktop"; Flags: nowait postinstall skipifsilent; Components: desktop

[Code]
function IsWebView2Installed: Boolean;
var
  InstallPath: string;
begin
  Result := RegQueryStringValue(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', InstallPath);
end;

var
  WebView2Page: TInputOptionWizardPage;

procedure InitializeWizard;
begin
  if not IsWebView2Installed then
  begin
    WebView2Page := CreateInputOptionPage(wpSelectComponents,
      'WebView2 Runtime', 'Optional component for in-app browser',
      'ShipRight Desktop uses WebView2 to display the dashboard inside the application window.'#13#10#13#10 +
      'If not installed, the dashboard will open in your default browser instead.',
      False, False);
    WebView2Page.Add('Install WebView2 Runtime (recommended)');
    WebView2Page.Values[0] := True;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  DownloadPath: string;
  ResultCode: Integer;
begin
  if (CurStep = ssPostInstall) and (WebView2Page <> nil) and WebView2Page.Values[0] then
  begin
    DownloadPath := ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe');
    if not FileExists(DownloadPath) then
    begin
      if Exec('powershell.exe', '-Command "& {Invoke-WebRequest -Uri ''https://go.microsoft.com/fwlink/p/?LinkId=2124703'' -OutFile ''' + DownloadPath + '''}"',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      begin
        if ResultCode <> 0 then
        begin
          MsgBox('Failed to download WebView2 Runtime. You can download it manually from https://developer.microsoft.com/microsoft-edge/webview2/', mbError, MB_OK);
          Exit;
        end;
      end;
    end;
    if FileExists(DownloadPath) then
    begin
      if Exec(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe'), '/silent /install', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
      begin
        if ResultCode = 0 then
          MsgBox('WebView2 Runtime installed successfully. ShipRight will use it next launch.', mbInformation, MB_OK)
        else
          MsgBox('WebView2 installation may have failed (exit code: ' + IntToStr(ResultCode) + ').', mbError, MB_OK);
      end;
    end;
  end;
end;
