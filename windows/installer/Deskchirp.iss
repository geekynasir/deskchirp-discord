; Build with scripts/build-windows-installer.ps1. Keep AppId stable across versions.
#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifndef InstallerDir
  #error InstallerDir is required
#endif

[Setup]
AppId={{F5C10229-3D01-49BF-9655-BF2D2564B917}
AppName=Deskchirp for Discord
AppVersion={#AppVersion}
AppVerName=Deskchirp for Discord {#AppVersion}
VersionInfoVersion={#AppNumericVersion}
VersionInfoProductVersion={#AppNumericVersion}
AppPublisher=geekynasir
AppPublisherURL=https://github.com/geekynasir/deskchirp-discord
AppSupportURL=https://github.com/geekynasir/deskchirp-discord/issues
AppUpdatesURL=https://github.com/geekynasir/deskchirp-discord/releases
DefaultDirName={localappdata}\Programs\Deskchirp
DefaultGroupName=Deskchirp
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
WizardStyle=modern
DisableWelcomePage=no
DisableReadyPage=yes
Compression=lzma2
SolidCompression=yes
OutputDir={#InstallerDir}
OutputBaseFilename=Deskchirp-{#AppVersion}-windows-x64-setup
UninstallDisplayIcon={app}\Deskchirp.exe
UninstallDisplayName=Deskchirp for Discord
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
VersionInfoCompany=geekynasir
VersionInfoDescription=Deskchirp for Discord Setup
VersionInfoProductName=Deskchirp for Discord
VersionInfoProductTextVersion={#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel1=Install Deskchirp for Discord
WelcomeLabel2=Share the developer app you're using with friends on Discord.%n%nSetup installs Deskchirp for your Windows account. You can choose what to share after opening the app.
FinishedHeadingLabel=Deskchirp is ready
FinishedLabel=Open Deskchirp from Start whenever you need it. It stays in the notification area when you close its settings.

[Tasks]
Name: "desktopicon"; Description: "Add a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,settings*.png"

[Icons]
Name: "{userprograms}\Deskchirp"; Filename: "{app}\Deskchirp.exe"; WorkingDir: "{app}"; Comment: "Share your developer activity on Discord"
Name: "{userdesktop}\Deskchirp"; Filename: "{app}\Deskchirp.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Deskchirp.exe"; Description: "Open Deskchirp"; Flags: nowait postinstall skipifsilent runasoriginaluser
