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
;   5. 简体中文语言包内置在 scripts\innosetup\ 下(Inno 官方不内置中文)，
;      保证任何机器/CI 打出来的向导都是中文。

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
; 安装目录可选(给想装到 D 盘的人)
DefaultDirName={localappdata}\{#AppName}
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

[Languages]
; 用仓库内置的中文语言包(见 scripts\innosetup\)。
; 不要再改回 compiler:Default.isl —— 那是英文，向导按钮会变英文。
Name: "chinesesimplified"; MessagesFile: "innosetup\ChineseSimplified.isl"

[Tasks]
; 桌面快捷方式默认不勾:国内用户对"装完桌面多一个图标"容易反感
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"
; 开始菜单快捷方式默认勾:绿色包最大的体验缺口就是没有它
Name: "startmenuicon"; Description: "创建开始菜单快捷方式"; GroupDescription: "附加任务:"

[Files]
; 整个 publish-beta 目录(主程序、更新器、easytier/、使用说明.txt)
; Excludes *.pdb:发布包不带调试符号，省体积也免得用户困惑
Source: "..\publish-beta\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

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