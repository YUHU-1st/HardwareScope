[Setup]
AppId={{62D55DB3-C6DB-4B9E-BB33-A193A8D2410D}
AppName=HardwareScope FileGuard 文件占用
AppVersion=1.1.0
AppPublisher=HardwareScope
DefaultDirName={autopf}\HardwareScope FileGuard
DisableDirPage=yes
DefaultGroupName=HardwareScope FileGuard
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\artifacts\release\1.3.0
OutputBaseFilename=HardwareScope-FileGuard-1.1.0-windows-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\HardwareScope.FileGuard.exe
DisableProgramGroupPage=yes
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\artifacts\publish\fileguard-x64\HardwareScope.FileGuard.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\docs\文件占用模块.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\docs\文件占用验证报告.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\docs\界面更新验证.md"; DestDir: "{app}"; Flags: ignoreversion

[Tasks]
Name: "autostart"; Description: "开机自动启动安静后台服务（推荐）"

[Icons]
Name: "{group}\文件占用查看与设置"; Filename: "{app}\HardwareScope.FileGuard.exe"

[Registry]
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\HardwareScope.FileGuard.exe"; ValueType: string; ValueName: ""; ValueData: "{app}\HardwareScope.FileGuard.exe"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\*\shell\HardwareScope.FileGuard"; ValueType: string; ValueName: ""; ValueData: "查看文件占用"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\*\shell\HardwareScope.FileGuard"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\HardwareScope.FileGuard.exe,0"
Root: HKLM; Subkey: "Software\Classes\*\shell\HardwareScope.FileGuard"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Single"
Root: HKLM; Subkey: "Software\Classes\*\shell\HardwareScope.FileGuard\command"; ValueType: string; ValueName: ""; ValueData: """{app}\HardwareScope.FileGuard.exe"" /inspect ""%1"""
Root: HKLM; Subkey: "Software\Classes\Directory\shell\HardwareScope.FileGuard"; ValueType: string; ValueName: ""; ValueData: "查看文件夹占用"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\Directory\shell\HardwareScope.FileGuard"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\HardwareScope.FileGuard.exe,0"
Root: HKLM; Subkey: "Software\Classes\Directory\shell\HardwareScope.FileGuard"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Single"
Root: HKLM; Subkey: "Software\Classes\Directory\shell\HardwareScope.FileGuard\command"; ValueType: string; ValueName: ""; ValueData: """{app}\HardwareScope.FileGuard.exe"" /inspect ""%1"""

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var Code: Integer;
begin
  Result := '';
  if FileExists(ExpandConstant('{app}\HardwareScope.FileGuard.exe')) then
    if not Exec(ExpandConstant('{app}\HardwareScope.FileGuard.exe'), '/config stop', '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
      Result := '无法停止已有文件占用服务，请关闭占用查看窗口后重试。';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var Code: Integer; Args: String;
begin
  if CurStep = ssPostInstall then begin
    if WizardIsTaskSelected('autostart') then Args := '/config install-auto'
    else Args := '/config install-manual';
    if not Exec(ExpandConstant('{app}\HardwareScope.FileGuard.exe'), Args, '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
      RaiseException('后台服务安装失败，错误码 ' + IntToStr(Code) + '。请重新运行安装程序。');
  end;
end;

function InitializeUninstall(): Boolean;
var Code: Integer;
begin
  Result := Exec(ExpandConstant('{app}\HardwareScope.FileGuard.exe'), '/config uninstall', '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
  if not Result then MsgBox('无法删除后台服务，请重试卸载。', mbError, MB_OK);
end;
