; Installer pentru aplicatia TailControl pe Windows (MAUI unpackaged, self-contained).
; Per utilizator, fara drepturi de administrator: cheia privata e oricum legata de contul curent
; (SecureStorage / DPAPI). Compilare: build-installer.ps1.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\artifacts\client-windows-staging"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\artifacts"
#endif

[Setup]
AppId={{B3E0D7A2-91C4-4F6A-8E25-7D1A9C0F4B62}
AppName=TailControl
AppVersion={#AppVersion}
AppPublisher=TailControl
AppPublisherURL=https://github.com/papura-octavian/tailcontrol
DefaultDirName={localappdata}\Programs\TailControl
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=TailControl-Client-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName=TailControl
UninstallDisplayIcon={app}\TailControl.Client.exe
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "Scurtatura pe Desktop"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\TailControl"; Filename: "{app}\TailControl.Client.exe"
Name: "{userdesktop}\TailControl"; Filename: "{app}\TailControl.Client.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\TailControl.Client.exe"; Description: "Porneste TailControl"; Flags: nowait postinstall skipifsilent
