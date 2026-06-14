; ShipRight InnoSetup installer
; Single command: iscc.exe shipright.iss
; Builds frontend + backend from source, then packages into setup.exe

#define AppName "ShipRight"
#define AppVersion "1.3.4"
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

; 3. Clean and copy wwwroot from front-end/out/ to back-end/ShipRight/wwwroot/
#define CleanWwwResult = Exec("cmd.exe", "/c if exist """ + S + "\back-end\ShipRight\wwwroot"" rd /s /q """ + S + "\back-end\ShipRight\wwwroot""", "", 0, 1)
#define CopyWwwResult = Exec("cmd.exe", "/c robocopy """ + S + "\front-end\out"" """ + S + "\back-end\ShipRight\wwwroot"" /e /nfl /ndl & exit 0", "", 0, 1)

; 4. Publish .NET backend as single-file self-contained exe
#define PublishResult = Exec("dotnet", "publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true """ + S + "\back-end\ShipRight\ShipRight.csproj"" -o """ + S + "\publish\win-x64""", "", 1, 1)
#if PublishResult != 0
  #error ".NET publish failed (exit code " + Str(PublishResult) + ")"
#endif

; ---- Compilation phase ----

[Setup]
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\ShipRight
DefaultGroupName=ShipRight
OutputDir={#OutputDir}
OutputBaseFilename=ShipRight-Setup-{#AppVersion}-win-x64
SetupIconFile=back-end\ShipRight\shipright.ico
UninstallDisplayIcon={app}\ShipRight.exe
Compression=lzma2
SolidCompression=yes

[Files]
Source: "publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
Source: "back-end\ShipRight\shipright.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\ShipRight"; Filename: "{app}\ShipRight.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\ShipRight"; Filename: "{app}\ShipRight.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\ShipRight.exe"; Description: "Launch ShipRight"; Flags: nowait postinstall skipifsilent
