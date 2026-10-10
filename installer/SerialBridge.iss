; SerialBridge Windows 安装包脚本（Inno Setup 6.5+）
; 构建命令：ISCC.exe installer\SerialBridge.iss
; 前置条件：先执行 dotnet publish 生成 dist\publish 目录（见 docs\编译与打包说明.md）

#define AppName "SerialBridge"
#define AppVersion "1.0.0"
#define AppPublisher "SerialBridge Project"
#define AppExeName "SerialBridge.exe"

[Setup]
AppId={{7A5C2E41-3B8D-4F6A-9C1E-SERIALBRIDGE1}
AppName={#AppName} 串口网络桥接工具
AppVersion={#AppVersion}
AppVerName={#AppName} v{#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\dist\installer
OutputBaseFilename=SerialBridge-Setup-{#AppVersion}-x64
SetupIconFile=..\src\SerialBridge.App\Assets\app.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
UninstallDisplayIcon={app}\{#AppExeName}
MinVersion=10.0

[Languages]
Name: "chinesesimplified"; MessagesFile: "{#SourcePath}\ChineseSimplified.isl"

[Tasks]
; 默认勾选创建桌面快捷方式（Inno Setup 任务默认即为选中状态）
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\dist\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 卸载时不删除 %AppData%\SerialBridge（用户配置与日志），由用户手动决定是否清理

[Messages]
SelectDirDesc=选择安装位置
SelectDirLabel3=安装程序将把 [name] 安装到以下文件夹。
FinishedLabelNoIcons=安装完成。%n%n[userename] 已安装到 [name]。您的映射配置保存在 %AppData%\SerialBridge，升级或卸载时自动保留。
FinishedLabel=安装完成。%n%n[userename] 已安装到 [name]。您的映射配置保存在 %AppData%\SerialBridge，升级或卸载时自动保留。

[Code]
// 卸载时询问是否删除用户配置（%AppData%\SerialBridge）
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: string;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{userappdata}') + '\SerialBridge';
    if DirExists(DataDir) then
    begin
      if MsgBox('是否同时删除用户配置与日志文件？' #13#10 +
                '(位于 ' + DataDir + '，删除后映射配置将丢失)',
                mbConfirmation, MB_YESNO) = IDYES then
      begin
        DelTree(DataDir, True, True, True);
      end;
    end;
  end;
end;

// 安装前若程序正在运行，提示并自动关闭
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  if CheckForMutexes('Global\SerialBridge.SingleInstance') then
  begin
    if MsgBox('检测到 SerialBridge 正在运行。是否关闭它以继续安装？', mbConfirmation, MB_YESNO) = IDYES then
    begin
      Exec('taskkill', '/IM SerialBridge.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Sleep(500);
    end
    else
    begin
      Result := '安装已取消：请先退出 SerialBridge 再运行安装程序。';
    end;
  end;
end;
