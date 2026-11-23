#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef NumericVersion
  #error NumericVersion is required
#endif
#ifndef PublishDirectory
  #error PublishDirectory is required
#endif

[Setup]
AppId={{A32EA87E-E680-4863-8696-D0502CE478C8}
AppName=Discoscope
AppVersion={#AppVersion}
AppPublisher=Stanislav SAVELIEV
AppPublisherURL=https://github.com/cybou-fr/Discoscope
DefaultDirName={localappdata}\Programs\Discoscope
DefaultGroupName=Discoscope
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19045
SetupIconFile=..\src\Discoscope.Desktop\Assets\appicon.ico
UninstallDisplayIcon={app}\Discoscope.exe
VersionInfoVersion={#NumericVersion}
CloseApplications=no
RestartApplications=no
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
OutputBaseFilename=Discoscope-{#AppVersion}-win-x64-setup

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Files]
Source: "{#PublishDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Discoscope"; Filename: "{app}\Discoscope.exe"; WorkingDir: "{app}"
