[Setup]
AppId={{53C0229E-859B-4084-8085-8069988BACEF}
AppName=Mouse Profiles
AppVersion=1.0
AppPublisher=Mouse Profiles
DefaultDirName={autopf}\MouseProfiles
DefaultGroupName=Mouse Profiles
UninstallDisplayIcon={app}\MouseProfiles.exe
SetupIconFile=..\MouseProfiles\Assets\mouse.ico
OutputDir=output
OutputBaseFilename=MouseProfilesSetup
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=lowest
ArchitecturesInstallIn64BitMode=x64compatible
AppMutex=MouseProfiles.SingleInstance

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\Mouse Profiles"; Filename: "{app}\MouseProfiles.exe"
Name: "{autodesktop}\Mouse Profiles"; Filename: "{app}\MouseProfiles.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Run]
Filename: "{app}\MouseProfiles.exe"; Description: "Launch Mouse Profiles"; Flags: nowait postinstall skipifsilent

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "MouseProfiles"; Flags: uninsdeletevalue

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  RegKey: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RegKey := 'Software\Microsoft\Windows\CurrentVersion\Run';
    RegDeleteValue(HKEY_CURRENT_USER, RegKey, 'MouseProfiles');
  end;
end;
