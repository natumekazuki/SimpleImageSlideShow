#include "AppVersion.ispp"

#define PublishDir "publish"
#define AppExecutable PublishDir + "\SimpleImageSlideShow.exe"
#define OutputFolder "artifacts"

#if FileExists(AppExecutable) == 0
  #error "Publish output not found at: {#AppExecutable}. Run dotnet publish (see installer/README.md) before building the installer."
#endif

[Setup]
AppId={{3A98B375-D19A-4ABB-B881-9A2CE2B982EF}}
AppName=SimpleImageSlideShow
AppVersion={#AppVersion}
AppVerName=SimpleImageSlideShow {#AppVersion}
AppPublisher=SimpleImageSlideShow
DefaultDirName={autopf}\SimpleImageSlideShow
DefaultGroupName=SimpleImageSlideShow
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\SimpleImageSlideShow.exe
OutputDir={#OutputFolder}
OutputBaseFilename=SimpleImageSlideShow_{#AppVersion}
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "japanese"; MessagesFile: "compiler:Languages/Japanese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\SimpleImageSlideShow"; Filename: "{app}\SimpleImageSlideShow.exe"
Name: "{autodesktop}\SimpleImageSlideShow"; Filename: "{app}\SimpleImageSlideShow.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\SimpleImageSlideShow.exe"; Description: "{cm:LaunchProgram,SimpleImageSlideShow}"; Flags: nowait postinstall skipifsilent