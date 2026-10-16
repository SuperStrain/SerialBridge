; SerialBridge Windows 安装包脚本（Inno Setup 6.5+）
; 构建命令：ISCC.exe installer\SerialBridge.iss
; 前置条件：先执行 dotnet publish 生成 dist\publish 目录（见 docs\编译与打包说明.md）

#define AppName "SerialBridge"
#define AppVersion "1.3.7"
#define AppPublisher "SerialBridge Project"
#define AppExeName "SerialBridge.exe"

[Setup]
AppId={{7A5C2E41-3B8D-4F6A-9C1E-8E3D2A1B4C5D}
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
CloseApplications=yes
RestartApplications=no
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
Filename: "{app}\{#AppExeName}"; Flags: nowait skipifdoesntexist; Check: IsSilentUpdate

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
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    // 先关闭运行中的程序：文件被占用会导致卸载不完整（残留文件与快捷方式）
    Exec('taskkill', '/IM SerialBridge.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(800);
  end;
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

// 自动升级成功后显式启动新版本；普通静默安装不会自动启动。
function IsSilentUpdate: Boolean;
begin
  Result := WizardSilent and
            (CompareText(ExpandConstant('{param:SERIALBRIDGEUPDATE|0}'), '1') = 0);
end;
