; ShipRight InnoSetup installer
; Single command: iscc.exe shipright.iss
; Builds frontend + backend from source, then packages into setup.exe

#define AppName "ShipRight"
#define AppVersion "1.0.0"
#define AppPublisher "ShipRight"
#define OutputDir "installer"

; ---- Pre-build steps (ISPP Exec runs during preprocessing, before compilation) ----

; Clean previous publish output
#define CleanPublishResult = Exec("cmd.exe", "/c if exist """ + SourcePath + "\publish\win-x64"" rd /s /q """ + SourcePath + "\publish\win-x64""", "", 0, 1)

; 1. Generate icon files
#define IconResult = Exec("cmd.exe", "/c npm run generate-icon", SourcePath + "\front-end", 0, 1)
#if IconResult != 0
  #error "Icon generation failed (exit code " + Str(IconResult) + ")"
#endif

; 2. Build Next.js frontend (outputs to front-end/out/)
#define FrontendResult = Exec("cmd.exe", "/c npm run build", SourcePath + "\front-end", 1, 1)
#if FrontendResult != 0
  #error "Frontend build failed (exit code " + Str(FrontendResult) + ")"
#endif

; 3. Clean and copy wwwroot from front-end/out/ to back-end/ShipRight/wwwroot/
#define CleanWwwResult = Exec("cmd.exe", "/c if exist """ + SourcePath + "\back-end\ShipRight\wwwroot"" rd /s /q """ + SourcePath + "\back-end\ShipRight\wwwroot""", "", 0, 1)
#define CopyWwwResult = Exec("cmd.exe", "/c xcopy /e /i /y """ + SourcePath + "\front-end\out\*"" """ + SourcePath + "\back-end\ShipRight\wwwroot\\""", "", 0, 1)
#if CopyWwwResult != 0
  #error "Copy wwwroot failed (exit code " + Str(CopyWwwResult) + ")"
#endif

; 4. Publish .NET backend as single-file self-contained exe
#define PublishResult = Exec("dotnet", "publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true """ + SourcePath + "\back-end\ShipRight\ShipRight.csproj"" -o """ + SourcePath + "\publish\win-x64""", "", 1, 1)
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
