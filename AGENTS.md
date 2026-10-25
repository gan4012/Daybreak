# AGENTS.md — Daybreak 破晓启动器(代理商交接文档)

> 本文件是给 AI 编码助手的完整交接文档。修改本仓库前**必须完整阅读**。
> 最后更新:2026-10-26。配套文档:`D:\ZCode-Data\launcher-plan\计划.md`(总体里程碑)。

## 项目是什么

自主品牌 Minecraft Java 版启动器,仅 Windows,C#/.NET 10 + WPF。
对标 PCL2 的功能丰富度,但**代码、UI、品牌全部自主**——严禁参考或复制 PCL 闭源代码。
目标用户为国内玩家:离线 + 微软正版 + Yggdrasil 外置登录三合一。

## 构建与验证命令

```powershell
# 构建(注意 .slnx 新格式)
dotnet build Daybreak.slnx -c Debug

# 测试(43 项:35 离线单元 + 8 联网集成;集成测试带 [Trait("Category","Integration")])
dotnet test Daybreak.slnx -c Debug --no-build

# 只跑离线快速测试(CI 用这个;集成测试会真下载游戏 jar / Java 运行时,约 5 分钟)
dotnet test Daybreak.slnx -c Debug --no-build --filter "Category!=Integration"

# 实机运行
cd Daybreak.Launcher/bin/Debug/net10.0-windows && ./Daybreak.Launcher.exe

# 内测包打包(自包含单文件 + 使用说明 → zip)
powershell -File scripts/package.ps1
```

## 硬性约束(违反会出真实事故,均已踩过)

1. **构建前必须杀掉运行中的实例**:`Get-Process Daybreak.Launcher | Stop-Process -Force`。
   运行中的 exe 锁 Core.dll 导致 MSB3027;还可能存在多个残留实例,截图验证前 tasklist 确认。
2. **所有 CmlLib 调用必须在 `Task.Run` 中**(脱离 UI 线程同步上下文)。
   CmlLib 内部有同步等待异步的代码,在 UI 线程上下文调用会死锁——表现为"正在准备游戏文件…"永久卡住。
3. **长下载的 HttpClient 必须 `Timeout = InfiniteTimeSpan`**(默认 100s 会掐断慢速大文件的响应体,
   CmlLib 安装器吞掉异常后静默挂起)。限速需求放在分片/尝试级 CTS 里实现
   (参考 ResilientHttpHandler 与 DownloadEngine 的做法:连接 15s、响应头 45s、分片 10 分钟)。
4. **游戏进程输出必须显式重定向**:CmlLib 构建的 Process 默认不重定向 stdout/stderr,
   游戏秒退时真因不可见。LaunchAsync 已强制重定向 + 行式 ReadLineAsync 读取,勿改回。
5. **Java 版本必须匹配**:EnsureJavaAsync 优先级 = 版本 JSON 声明的 javaVersion 组件 → 系统探测
   (JavaLocator.FindJava)→ 按大版本下载官方运行时。VM 层不得用 FindJava 短路跳过 Core 逻辑
   (曾导致 Adoptium Java 21 启动需要 Java 25 的 MC 26.2,秒退)。
6. **UI 线程不得被日志/进度事件淹没**:CmlLib 大版本安装触发 5558+ 文件事件、每秒数千字节事件。
   必须走 HomeViewModel 的日志节流机制(StringBuilder 缓冲 + 300ms DispatcherTimer 刷新)
   与 LaunchService.HookProgress 的 1MB 字节节流。任何新的事件接入都要同样节流。
7. **PowerShell 5.1 脚本**:含中文的 .ps1 必须 UTF-8 **带 BOM**;here-string 必须 CRLF 行尾
   (LF-only 的 here-string 在 PS 5.1 解析失败)。新脚本写完先转格式。
8. **品牌红线**:不碰 PCL 闭源代码、不用 "PCL" 字样、authlib-injector 是 AGPL(只读规范不抄代码)、
   引用的第三方库必须是 MIT/Apache 等宽松许可(改依赖前查许可证)。

## 架构

```
Daybreak.slnx(.NET 10 新格式解决方案)
├── Daybreak.Launcher      # WPF 主程序:Views(Pages)/ViewModels/Services/Themes
├── Daybreak.Core          # 启动核心:Game/Auth/Mod/Market/Diagnostics/Storage,不依赖 UI
├── Daybreak.Market        # Modrinth 客户端(预留扩展)
├── Daybreak.Updater       # 独立更新器(M5 占位)
└── Daybreak.Core.Tests    # xUnit(离线桩 + 联网集成测试分文件)
```

- **UI 与核心彻底分离**:Core 不引用任何 WPF 类型;VM 通过 `App.GetService<T>()`(MSDI)取依赖。
- **DI 注册**在 `Daybreak.Launcher/App.xaml.cs`:MainWindow/Auth/Settings 单例,其余 Transient
  (每次导航新实例,页面构造器里 `DataContext = App.GetService<XxxViewModel>()`)。
- **页面导航**:`MainWindow.xaml` 自建侧栏(ListBox + 自定义模板,非 NavigationView),
  选择项 → `ContentHolder(Frame).Content = App.GetService<pageType>()`。

### Core 各模块

| 模块 | 类 | 职责与要点 |
|---|---|---|
| Game/ | LaunchService | 启动门面:EnsureJavaAsync(组件优先)、InstallAsync(隔离感知)、LaunchAsync(Task.Run 由 VM 负责)、GetJavaComponentAsync |
| Game/ | ResilientHttpHandler | 每请求韧性:连接 15s/响应头 45s 限时,GET 重试 3 次并按 上次成功镜像→配置→另一镜像 降级;镜像健康记忆 _lastGoodMirror(静态) |
| Game/ | DownloadEngine | 自研分片下载:Range 分片并发、CopyExactlyAsync(不依赖流 EOF)、SHA1、.part 原子改名、批量并发 |
| Game/ | MojangJavaInstaller | piston-meta java-runtime 清单 → 逐文件下载+SHA1,布局 runtime/windows-x64/<组件>/(与 CmlLib resolver 一致,装过即复用) |
| Game/ | JavaLocator | 系统探测:JAVA_HOME + 六大发行版目录 |
| Auth/ | MicrosoftAuthenticator | MSAL 设备码流程;ClientId 常量待发布前换自有 Azure 应用 |
| Auth/ | YggdrasilClient | 自研 Yggdrasil 协议(ALI 发现/authenticate/validate/refresh),可注入 HttpClient 供测试打桩 |
| Auth/ | AuthlibInjectorDownloader | authlib-injector.jar:BMCLAPI 主源 + gh-proxy 兜底 + SHA256 |
| Auth/ | AccountStore | accounts.json 多账号(离线/微软/外置) |
| Mod/ | LoaderService | Fabric(Fabric Meta profile/json 改写 id)/ Forge / NeoForge(CmlLib Forge 安装器 2.0)安装 |
| Mod/ | ModInstallService | Mod 装入(隔离感知 mods 目录)+ 安装记录持久化(daybreak-mods.json)+ mrpack 导入 |
| Mod/ | CurseForgeImportService | CF 整合包免 key 导入:manifest → cfwidget 查文件名 → forgecdn 直链 → overrides 解压 |
| Market/ | ModrinthClient | API v2:搜索(facets)/版本/哈希反查 |
| Diagnostics/ | CrashAnalyzer | 12 条崩溃规则 → 人话建议,扫 crash-reports + latest.log |
| Storage/ | LauncherConfig / AccountStore | config.json / accounts.json,均有测试用 DirectoryOverrideForTests |

### UI 约定

- 框架:WPF-UI 4.3(`xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"`,FluentWindow/NavigationView 已弃用改自建侧栏)
- MVVM:CommunityToolkit.Mvvm([ObservableProperty]/[RelayCommand]),页面无业务逻辑
- 主题:Themes/Brand.xaml 集中管理黎明色 token + GlassCard/BrandPrimaryButton 样式;
  ThemesDictionary 由 ThemeService 按 config 切换(Dark/Light + Acrylic/Mica/None)
- 侧栏:宽 240(折叠 64),设计规格见用户 2026-10-25 的任务书(中性白 token,金色只给 logo)
- 版本管理/资源市场/下载页共用"底部进度条 + 状态 + 当前文件"模式

## 已知坑(WPF/CmlLib/环境)

- **WPF-UI 4.3 API**:无 UiPage(用标准 Page)、无 NavigationViewItem 覆盖键、
  ToggleSwitch 等 ui: 控件需 ui: 前缀、Title 文字与窗口按钮错位(用自定义 Header 修复)、
  无 PaneBackground 资源键;图标名先从 dll 字符串验证(如 Compass24 在 SymbolFilled 不在 SymbolRegular)
- **.slnx**:SDK 10 的 dotnet new sln 默认生成 .slnx;CLI 命令用 `Daybreak.slnx`
- **WPF-UI 4.3 的 WindowBackdropType** 在 Wpf.Ui.Controls 命名空间;无 ApplySystemTheme,
  用 SystemThemeManager.GetCachedSystemTheme 映射
- **MVersionSortOption** 是类不是枚举;**MArgument** 构造需确认;
  ParallelGameInstaller 直构可调并发(checker/downloader)
- **bmclapi2.bangbang93.com 从本机有时不可达**(TLS/连接黑洞):ResilientHttpHandler 已自动降级,
  集成测试一律用 official;cfwidget.com 免 key 可用但首次查询有 3~10s 排队
- **文档陷阱**:CmlLib 的 ProcessWrapper(StartWithEvents)只在 MLauncher 路径;
  InstallAndBuildProcessAsync 返回裸 System.Diagnostics.Process(无 OutputReceived 事件)

## 实机验证工作流

辅助脚本(仓库根):
- `capture.ps1` — 按 Daybreak 窗口矩形截图(勿全屏,会拍到用户桌面)
- `click.ps1 -dx x -dy y` — 窗口相对坐标点击
- `hover.ps1` / `uia-invoke.ps1 -name "按钮名"` — 悬停与 UIA 调用(部分 WPF-UI 按钮鼠标模拟无效时用)
- `nav-settings.ps1` / `dump-uia.ps1` — UIA 树诊断

流程:杀进程 → build → 启动 → click/hover/UIA → capture.ps1 → Read app.png。
验证后必须 Stop-Process 杀掉实例再构建(文件锁)。

## 当前进度(2026-10-26)

- **M0-M4 全部完成**,启动链路实机验证:**Minecraft 26.2 已由启动器成功拉起进主界面**
- 完善批次:版本管理页(删除/打开目录/Mod 清理/更新检查)、可编辑设置页、崩溃诊断(12 规则)、
  CurseForge 免 key 导入、安装记录持久化、内测打包脚本(package.ps1 → 70.6MB 自包含 zip)
- **未完成/已知缺口**:
  - Mod 更新检查无持久化 UI 入口(逻辑已在版本管理页,可从资源市场一键触发更佳)
  - CurseForge zip 导入未用真实整合包实测(逻辑同 mrpack,理论可用)
  - Forge/NeoForge 完整安装未实跑(管线与 Fabric 同,风险低)
  - 微软登录 ClientId 用的是 CmlLib 示例 id,**发布前必须换**(设置页已支持填写自有 ClientId)
- **M5 进展(2026-10-26 完成)**:
  - ✅ 仓库已建并推送:`https://github.com/gan4012/Daybreak`(私仓,SSH over 443)
  - ✅ 发布 CI(`.github/workflows/release.yml`):tag `v*` 触发 → build/test/package → 上传 Release
  - ✅ 自更新器骨架:`Daybreak.Updater --check / --update`,设置页有「检查更新」按钮
  - ✅ 微软 ClientId 配置化(设置页可填,留空回退开发期备用 ID)
  - ✅ CI 测试策略:集成测试打 `[Trait("Category","Integration")]`,CI 用 `--filter "Category!=Integration"` 只跑 35 项离线测试
  - ⚠️ 环境注意:本机推送走 SSH `ssh.github.com:443`(github.com:22 与 HTTPS+GCM 均不稳),配置在 `~/.ssh/config`
- **M5 剩余**:首次 Release 实跑验证、公测打包分发、自更新器端到端实测

## 提交规范

提交信息:类型 + 摘要(中文),正文列要点与测试情况。示例:
`M4 补全:CurseForge 整合包免 key 导入 + Mod 安装记录持久化 + 内测打包脚本`
测试情况必须如实(哪些实测、哪些未验证)。

## 修改代码前的检查清单

1. 读完本文件 + 相关模块源码
2. 改 Core 层先跑 `dotnet test`(勿在启动器运行时构建,文件锁)
3. 改 UI 后必须实机截图验证(capture.ps1),不能只看编译通过
4. 涉及下载/启动流程的改动,必须在控制台(engdbg 模式)或实机完整走一遍
5. 新增第三方依赖先查许可证(MIT/Apache 才行),并更新 THIRD-PARTY-NOTICES.md
