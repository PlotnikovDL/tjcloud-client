#ifndef AppVersion
  #error Build with scripts/Package.ps1 to supply repository metadata.
#endif

[Setup]
AppId={{12ca27e9-70d7-48f0-b0ac-350146b5578a}
AppName={#ProductName}
AppVersion={#AppVersion}
AppPublisher={#Publisher}
AppPublisherURL={#RepositoryUrl}
AppSupportURL={#RepositoryUrl}/issues
AppUpdatesURL={#RepositoryUrl}/releases
VersionInfoVersion={#AppVersion}.0
VersionInfoProductName={#ProductName}
VersionInfoProductVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\TJCloudClient
DefaultGroupName={#ProductName}
DisableProgramGroupPage=yes
AllowNoIcons=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDirectory}
OutputBaseFilename={#ArtifactStem}-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
UninstallDisplayName={#ProductName}
UninstallDisplayIcon={app}\TjCloudClient.exe
LicenseFile={#PublishDirectory}\LICENSE

[Files]
Source: "{#PublishDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#ProductName}"; Filename: "{app}\TjCloudClient.exe"
