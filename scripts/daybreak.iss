; Daybreak 破晓启动器 —— Inno Setup 安装包脚本
;
; 由 scripts/package-installer.ps1 调用:
;   ISCC.exe /DMyAppVersion=0.1.3 scripts\daybreak.iss
;
; 设计要点(不要随意改动):
;   1. 装到 %LOCALAPPDATA%\Daybreak —— 用户目录，不需要管理员。
;      这是关键:自更新器是在「自己 exe 所在目录」原地替换文件的，
;      装到用户可写目录后，更新链路一行代码都不用改、也不会弹 UAC。
;   2. PrivilegesRequired=lowest —— 全程不提权。
;   3. AppId 是固定 GUID —— 每次生成新的会导致升级时装出两份。
;   4. 卸载只删 {app}，绝不碰 %APPDATA%\Daybreak(配置/账号)
;      和游戏目录里的 daybreak-servers\(服务端实例) —— 那是用户的数据。

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

#define AppName "Daybreak"
#define AppNameCN "破晓启动器"
#define AppPublisher "Daybreak"
#define LauncherExe "Daybreak.Launcher.exe"

[Setup]
; 固定 GUID，不要改。改了会导致旧版本无法被识别，升级变并存安装。
AppId={{8F3A2C41-5B7E-4D92-A1C6-3E9B7D4F2A85}
AppName={#AppNameCN}
AppVersion={#MyAppVersion}
AppVerName={#AppNameCN} {#MyAppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\{#AppName}
DefaultGroupName={#AppNameCN}
DisableProgramGroupPage=yes
DisableDirPage=no
; 不需要管理员权限。改成 admin 会让自更新失效。
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=Daybreak-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#AppNameCN}
UninstallDisplayIcon={app}\{#LauncherExe}
SetupIconFile=..\src\Daybreak.Launcher\daybreak.ico
; 允许覆盖安装在已有目录上（升级场景）
AllowNoIcons=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Default.isl"

[Tasks]
; 桌面快捷方式默认不勾:国内用户对"装完桌面多一个图标"容易反感
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"; Flags: unchecked
; 开始菜单默认勾:绿色包最大的体验缺口就是没有它
Name: "startmenuicon"; Description: "创建开始菜单快捷方式"; GroupDescription: "附加任务:"; Flags: checkedonce

[Files]
; 整个 publish-beta 目录(主程序、更新器、easytier/、使用说明.txt)
Source: "..\publish-beta\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userdesktop}\{#AppNameCN}"; Filename: "{app}\{#LauncherExe}"; Tasks: desktopicon
Name: "{userprograms}\{#AppNameCN}"; Filename: "{app}\{#LauncherExe}"; Tasks: startmenuicon

[Run]
Filename: "{app}\{#LauncherExe}"; Description: "立即启动 {#AppNameCN}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 只删安装目录本身。
; 故意不删 %APPDATA%\Daybreak(配置与账号)和游戏目录里的服务端实例 ——
; 卸载程序删用户数据是恶习，而且删错了找不回来。
Type: filesandordirs; Name: "{app}"

[Code]
// 安装前提示:如果检测到已存在的安装目录，说明是升级，
// 提醒用户不要把它指到「绿色包解压出来的目录」上，避免混装。
function InitializeSetup(): Boolean;
begin
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  // 安装完成后不做额外动作。快捷方式由 [Icons] 处理，
  // 用户数据目录由主程序自己在首次运行时创建。
end;