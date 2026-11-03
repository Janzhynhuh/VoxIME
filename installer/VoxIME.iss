; VoxIME 声语安装包（Inno Setup 6）
; 默认安装到当前用户目录，不需要管理员权限；配置/历史/模型保留在 %APPDATA%\VoxIME。

#define AppName "VoxIME 声语"
#define AppShortName "VoxIME"
#define AppPublisher "VoxIME Project"
#define AppUrl "https://github.com/Janzhynhuh/VoxIME"

#ifndef AppVersion
  #define AppVersion "0.3.4"
#endif

#ifndef SourceDirectory
  #define SourceDirectory "..\deploy\VoxIME-win-x64"
#endif

#ifndef OutputDirectory
  #define OutputDirectory "..\deploy"
#endif

[Setup]
AppId={{62D8F609-0D50-43B0-8BC9-0E8CA6741EC8}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={localappdata}\Programs\{#AppShortName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDirectory}
OutputBaseFilename=VoxIME-Setup-v{#AppVersion}
SetupIconFile=..\assets\app.ico
UninstallDisplayIcon={app}\VoxIME.exe
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter=VoxIME.exe
RestartApplications=no
SetupLogging=yes
LicenseFile=..\LICENSE
VersionInfoVersion={#AppVersion}.0
VersionInfoProductVersion={#AppVersion}
VersionInfoDescription={#AppName} 安装程序
VersionInfoCompany={#AppPublisher}
VersionInfoCopyright=Copyright © 2026 VoxIME Project
UsePreviousAppDir=yes
UsePreviousTasks=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: unchecked
Name: "removedata"; Description: "卸载时同时删除配置、历史和模型数据（%APPDATA%\VoxIME）"; GroupDescription: "附加任务："; Flags: unchecked

[Files]
Source: "{#SourceDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\VoxIME.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\VoxIME.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\VoxIME.exe"; Description: "启动 {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM VoxIME.exe /F"; Flags: runhidden; RunOnceId: "StopVoxIME"

[Code]
const
  InstallerKey = 'Software\VoxIME\Installer';
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    RegWriteDWordValue(
      HKCU,
      InstallerKey,
      'RemoveUserDataOnUninstall',
      Ord(WizardIsTaskSelected('removedata')));
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  RemoveData: Cardinal;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // 删除应用自己写入的开机启动项，避免卸载后残留。
    RegDeleteValue(HKCU, RunKey, 'VoxIME');

    // 默认保留用户数据；只有安装时勾选 remove data 才清理。
    if RegQueryDWordValue(HKCU, InstallerKey, 'RemoveUserDataOnUninstall', RemoveData)
       and (RemoveData <> 0) then
    begin
      DelTree(ExpandConstant('{userappdata}\VoxIME'), True, True, True);
    end;

    RegDeleteKeyIncludingSubkeys(HKCU, InstallerKey);
  end;
end;
