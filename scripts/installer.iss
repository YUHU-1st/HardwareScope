[Setup]
AppId={{3A03C035-1110-41D5-B153-0F6C06203061}
AppName=HardwareScope 硬件视界
AppVersion=1.2.0
AppPublisher=HardwareScope
DefaultDirName={localappdata}\Programs\HardwareScope
DefaultGroupName=HardwareScope 硬件视界
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\release\1.2.0
OutputBaseFilename=HardwareScope-1.2.0-windows-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\HardwareScope.exe
CloseApplications=yes
RestartApplications=no
DisableProgramGroupPage=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\artifacts\publish\windows-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked
Name: "fileguard"; Description: "安装文件占用右键菜单与后台服务（需要管理员授权）"

[Icons]
Name: "{group}\HardwareScope 硬件视界"; Filename: "{app}\HardwareScope.exe"
Name: "{autodesktop}\HardwareScope 硬件视界"; Filename: "{app}\HardwareScope.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\HardwareScope.exe"; Description: "启动 HardwareScope 硬件视界"; Flags: nowait postinstall skipifsilent

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "HardwareScope"; Flags: uninsdeletevalue

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var Code: Integer;
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('fileguard') then
    if not ShellExec('runas', ExpandConstant('{app}\FileGuard\HardwareScope-FileGuard-1.1.0-windows-x64-setup.exe'), '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-', '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
      MsgBox('文件占用后台模块未安装。可在软件的“文件占用”页重新安装；错误码 ' + IntToStr(Code) + '。', mbError, MB_OK);
end;
