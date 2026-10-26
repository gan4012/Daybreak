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

# 测试(离线 118 项 + 联网集成 20 项;集成测试带 [Trait("Category","Integration")])
dotnet test Daybreak.slnx -c Debug --no-build

# 只跑离线快速测试(CI 用这个;集成测试会真下载游戏 jar / Java 运行时 / 服务端核心,较慢)
dotnet test Daybreak.slnx -c Debug --no-build --filter "Category!=Integration"

# 实机运行
cd src/Daybreak.Launcher/bin/Debug/net10.0-windows && ./Daybreak.Launcher.exe

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
   **实测坑**:用编辑工具(edit/write)改含中文的 .ps1 会**静默丢掉 BOM**,之后 `powershell -File` 报
   `Unexpected token ')'` 且错误信息里中文是乱码(如「完成」→「瀹屾垚」)。改完必须重新补 BOM:
   ```powershell
   $p="scripts\package.ps1"
   $t=[System.IO.File]::ReadAllText($p,(New-Object System.Text.UTF8Encoding($false)))
   $t=($t -replace "`r`n","`n") -replace "`n","`r`n"
   [System.IO.File]::WriteAllText($p,$t,(New-Object System.Text.UTF8Encoding($true)))
   ```
   校验:`ParseFile` 解析无错 + 前 3 字节为 `EF BB BF`。
8. **品牌红线**:不碰 PCL 闭源代码、不用 "PCL" 字样、authlib-injector 是 AGPL(只读规范不抄代码)、
   引用的第三方库必须是 MIT/Apache 等宽松许可(改依赖前查许可证)。

## 架构

```
Daybreak.slnx(.NET 10 新格式解决方案)
└── src/
    ├── Daybreak.Launcher      # WPF 主程序:Views(Pages)/ViewModels/Services/Themes
    ├── Daybreak.Core          # 启动核心:Game/Auth/Mod/Market/Diagnostics/Storage,不依赖 UI
    ├── Daybreak.Market        # Modrinth 客户端(预留扩展)
    ├── Daybreak.Updater       # 独立更新器:--check/--update;不引用 Core,发布时裁剪(11.5MB)
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
- **GitHub Release 资产名不能含非 ASCII**:上传时中文会被替换成 `.`
  (实测 `Daybreak-内测包-win-x64.zip` → `Daybreak-.-win-x64.zip`)。发布包名必须全 ASCII,
  否则自更新器按文件名匹配资产会失败。当前包名:`Daybreak-beta-win-x64.zip`。
- **Gitee 发行版 API 的 4 个坑**(全部实测,处理逻辑见 `scripts/publish-gitee.ps1`):
  1. `GET /releases/tags/{tag}` 在**发行版不存在时返回 HTTP 200 + 正文 `null`**(不是 404),
     靠异常判断会误判"已存在"。必须校验返回对象里有没有 `id`。
  2. `PATCH /releases/{id}` 更新标题/说明时**必须带 `tag_name`**,否则 400 Bad Request。
  3. JSON 里的中文若直接发 UTF-8 字节,Gitee 会**按 Latin-1 解码成乱码**
     (`首个内测版` → `é¦ä¸ªåæµç`)。必须转成 `\uXXXX` 纯 ASCII 转义再发。
     注意:PowerShell 控制台回显乱码但服务端存储正确,别被回显误导,用浏览器/API 原始 JSON 核对。
  4. 发行版 `assets` 里**没有 `size` 字段**(更新器读 size 需容错),
     且会自动附加源码包 `{tag}.zip` / `{tag}.tar.gz` —— 匹配资产时不能只看 `.zip` 后缀。
- **Gitee 资产不支持 Range**:请求带 `Range` 仍返回 200 而非 206,断点续传会退化为重下。
  实测 Gitee 下载 77MB 稳定 **1.45 MB/s**(53 秒完成,哈希与本地一致);
  同期 GitHub 直连多次在连接阶段超时。故更新器默认 **Gitee 优先 → GitHub 回退**。
- **现代 Forge/NeoForge 的版本目录里只有 JSON,没有客户端 jar**:jar 通过 `inheritsFrom` 指向
  原版版本(如 `versions/1.20.1/1.20.1.jar`),加载器只是往 `libraries/` 放 patch 产物。
  校验安装结果时必须沿继承链解析,不能直接找同名 jar(集成测试里有 `ResolveClientJar` 实现)。
- **NeoForge 安装不走镜像(已知限制)**:`NeoForgeInstaller` 只有一个构造函数
  `NeoForgeInstaller(MinecraftLauncher)`,**无法注入 HttpClient**,内部自带 HttpClient,
  因此 ResilientHttpHandler 的镜像改写与重试对它无效。
  实测国内直连 `maven.neoforged.net` 会在连接阶段超时(21s 后失败,无重试),
  而 BMCLAPI 已完整镜像该仓库(`https://bmclapi2.bangbang93.com/maven/net/neoforged/...`,已验证可取安装器 jar)。
  **影响**:国内用户默认装不了 NeoForge;Forge 不受影响(实测安装成功)。
  可选解法(未实施):① 预先把 BMCLAPI 上的安装器 jar 与依赖喂给 CmlLib;
  ② 等 CmlLib 支持注入;③ 引导用户配代理(设置页已支持)。
- **首次启动引导必须临时改 ShutdownMode**:WPF 默认 `OnLastWindowClose`,
  模态引导窗是引导期间唯一窗口,关闭它会让应用在 `MainWindow.Show()` 之前直接退出
  (表现为"走完引导启动器就消失",编译期完全看不出来)。
  引导前设 `ShutdownMode.OnExplicitShutdown`,显示主窗口后恢复 `OnMainWindowClose`。
- **XAML 根 Grid 的行数要和 Grid.Row 用到的最大索引对得上**:VersionsPage 只定义了 3 个
  RowDefinition 却用了 `Grid.Row="3"`(状态文字),WPF 会把它挤到最后一行,
  导致状态文字与 Mod 管理卡片**重叠**。改布局网格时务必数一遍行数。

### 联机功能(WPF-UI / 服务端)相关坑

- **图标名必须用反射验证,不能用 dll 字符串搜索**:我用字符串搜索判断 `Network24` "存在",
  结果它并不是 `SymbolRegular` 的枚举成员,XAML 解析直接抛
  `XamlParseException: Network24 is not a valid value for SymbolRegular`,
  表现为「点侧栏导航没反应、内容区不切换」(异常被 DispatcherUnhandledException 吞掉只写日志)。
  **正确做法**是写个反射小程序枚举真实成员(共 9235 个):
  ```csharp
  Enum.GetNames(typeof(SymbolRegular)).Contains("Globe24")
  ```
  已确认不存在:`Network24` / `Wifi24` / `World24`;存在:`VirtualNetwork24` / `Globe24` / `Earth24`。
- **Core 模型直接绑到 UI 不会刷新**:`SavedServer` 等 Core 类没有 `INotifyPropertyChanged`,
  改它的属性后界面无反应(实测:状态栏说"查询成功"、列表却还是灰点和「—」)。
  必须像 `SavedServerItemViewModel` 那样在 UI 层包一层可观察对象。
- **本项目不使用 IValueConverter**,条件显示统一走 `DataTrigger` + VM 暴露的布尔属性
  (如 `HasSelectedServer`/`IsServersTab`/`NeedsEula`)。新增页面请沿用,不要引入转换器。
- **Forge 1.17+ 服务端不能用 `java -jar run.bat` 启动**:安装器产出的 run.bat 内容是
  `java @user_jvm_args.txt @libraries/net/minecraftforge/forge/<ver>/win_args.txt %*`,
  正确做法是从 run.bat 解析 `@` 引用再拼命令行(见 `ServerManager.ExtractAtArguments`)。
  解析时**要排除 `@echo` 这类 batch 指令**(否则会传给 java 一个无效的 `@echo` 参数),
  做法是只接受真实存在的 `.txt` 参数文件。
- **`DownloadEngine.DownloadFileAsync` 的 expectedSize 语义**:`null`=大小未知、`0`=**已知空文件**。
  传 0 表达"未知"会得到一个 0 字节文件,直到服务端启动报
  `Invalid or corrupt jarfile` 才暴露(编译期与单元测试都发现不了)。
- **Fabric 服务端必须同时准备原版 server.jar**:`fabric-server-launch.jar` 依赖同目录的
  原版 jar 做运行时补丁(`fabric-server-launcher.properties` 的 `serverJar` 项)。
- **Minecraft 服务器地址必须查 SRV 记录**:`_minecraft._tcp.<域名>`。
  实测 `2b2t.org` 的真实入口是 `connect.2b2t.org:25565`,直连 A 记录会打到 Cloudflare 上然后超时。
  **.NET 的 `System.Net.Dns` 不支持 SRV 查询**,`SrvResolver` 是手写的 DNS 报文
  (UDP 53,含压缩指针解析、按系统 DNS 服务器轮询)。握手里仍填**原始域名**而非 SRV 目标。
- **各服务端下载源(2026-10-26 全部实测)**:
  | 类型 | 端点 | 备注 |
  |---|---|---|
  | Vanilla | 版本 JSON 的 `downloads.server` | 带 SHA1,BMCLAPI 有镜像 |
  | Fabric | `meta.fabricmc.net/v2/versions/loader/{游戏}/{加载器}/{安装器}/server/jar` | **必须 4 段路径**,3 段会 404 |
  | Paper | `fill.papermc.io/v3/projects/paper/...` | **旧 API `api.papermc.io/v2` 已下线(410)**;带 SHA256;`/versions/{ver}` 还给出官方推荐 JVM 参数 |
  | Forge | Forge 安装器 + `--installServer` | **BMCLAPI 对该安装器返回 403**,必须走官方源 |
- **服务端进程管理要点**:必须 `RedirectStandardInput=true`(靠 stdin 发 `stop`/`op` 等命令);
  停服先发 `stop` 命令、超时才强杀(直接杀会导致世界未保存);日志量比游戏更大,
  UI 侧必须节流(实测用 300ms DispatcherTimer 批量刷,并只保留尾部 500 行)。
- **服务端实例存储布局**:`<游戏目录>/daybreak-servers/<名称>/`(含 server.jar/world/logs),
  索引在 `<游戏目录>/daybreak-servers.json`;收藏的服务器列表在 `<游戏目录>/daybreak-serverlist.json`。

## 实机验证工作流

辅助脚本(`tools/`):
- `tools/capture.ps1` — 按 Daybreak 窗口矩形截图(勿全屏,会拍到用户桌面)
- `tools/click.ps1 -dx x -dy y` — 窗口相对坐标点击
- `tools/hover.ps1` / `tools/uia-invoke.ps1 -name "按钮名"` — 悬停与 UIA 调用(部分 WPF-UI 按钮鼠标模拟无效时用)
- `tools/nav-settings.ps1` / `tools/dump-uia.ps1` — UIA 树诊断

流程:杀进程 → build → 启动 → click/hover/UIA → tools/capture.ps1 → Read app.png。
验证后必须 Stop-Process 杀掉实例再构建(文件锁)。

## 当前进度(2026-10-26)

- **M0-M4 全部完成**,启动链路实机验证:**Minecraft 26.2 已由启动器成功拉起进主界面**
- 完善批次:版本管理页(删除/打开目录/Mod 清理/更新检查)、可编辑设置页、崩溃诊断(12 规则)、
  CurseForge 免 key 导入、安装记录持久化、内测打包脚本(package.ps1 → 77MB 自包含 zip,含主程序+更新器)
- **未完成/已知缺口**:
  - 微软登录 ClientId 用的是 CmlLib 示例 id,**发布前必须换**(设置页已支持填写自有 ClientId)
  - **NeoForge 安装不走镜像**:国内网络默认装不了(详见"已知坑";CmlLib 无法注入 HttpClient)
  - Gitee 发行版附件容量上限尚未确认(当前 3 个版本约 230MB,未触发限制)
- **第三阶段验证完成(2026-10-26)**——此前"理论可用但未实测"的路径已逐个实跑:
  - ✅ **Forge 1.20.1 真实安装**(`1.20.1-forge-47.4.28`):完整跑通 processors 执行链
    (下载原版 → 官方 Java 17 → Forge 安装器 → binarypatcher 打补丁),2 分 15 秒通过
  - ✅ **CurseForge 整合包导入**:用真实项目 ID(JEI `238222`)经 cfwidget 查到真实 fileID,
    构造 CF 规范 zip → 下载 4.6MB jar 到 mods/ + overrides 正确解压,3 秒通过
  - ❌ **NeoForge 安装**:被 maven.neoforged.net 不可达阻塞(非代码问题,已加连通性预检与排查提示)
  - 📌 副产品修复:DownloadMirror 新增 Forge/NeoForge Maven 改写规则(含 4 个单元测试),
    Forge 路径因此获得 BMCLAPI 加速
- **体验补完(2026-10-26)**:
  - ✅ 首次启动引导:三步向导(游戏目录 → 内存/镜像 → 完成),能识别已有 .minecraft 目录并提示复用;
    仅对"真首次"(连 config.json 都不存在)弹出,老用户静默跳过
  - ✅ Mod 列表搜索/排序:按文件名搜索,支持 名称/大小/更新状态 三种排序(已实机截图验证)
  - ✅ Mod 一键更新:可更新项旁出现「更新」按钮,支持「全部更新」批量处理(下载新版 → 删旧文件 → 刷新记录)
  - 🐞 实机发现并修复:引导后主程序不启动(ShutdownMode)、版本页状态文字与 Mod 卡片重叠(缺 RowDefinition)
- **联机辅助功能(2026-10-26 完成)**:四种服务端全部实测可开服,联机页三 Tab 已实机截图验证
  - ✅ **Vanilla / Fabric / Paper / Forge 四种服务端端到端打通**(集成测试各跑一次真实开服):
    从下载核心 → EULA 校验 → 启动 → 就绪日志 `Done (...)` → 端口监听 → 控制台命令 → 优雅停服
  - ✅ 服务端实例存 `<游戏目录>/daybreak-servers/<名称>/`,索引 `daybreak-servers.json`
  - ✅ server.properties 可视化读写(**保留注释与未知键**);EULA 必须用户显式勾选
  - ✅ 控制台:stdin 命令通道(`op`/`list`/`whitelist` 等)+ 日志节流(300ms 批量刷、尾部 500 行)
  - ✅ 服务器列表:收藏(持久化)、SLP 查在线人数与延迟、**一键进服**(`--server/--port` 直连)
  - ✅ 局域网:监听 Minecraft 广播(组播 224.0.2.60:4445)+ 网段扫描兜底
  - ✅ **新增 SrvResolver**:手写 DNS SRV 查询(.NET 的 `Dns` 类不支持),
    修复了 2b2t.org 这类"真实入口在 SRV 记录里"的服务器直连必超时的问题
  - 🐞 实机发现并修复:图标名 `Network24` 不存在导致整页 XAML 解析失败、
    Core 模型无 INPC 导致查询结果不刷新、Forge run.bat 启动方式、DownloadEngine 大小语义、
    Fabric 缺原版 jar、`@echo` 被误当参数文件
  - 📌 未验证:一键进服的真实拉起(需要已安装的游戏版本)、局域网广播在两台真机间的发现
- **M5 进展(2026-10-26 完成)**:
  - ✅ 仓库:GitHub `https://github.com/gan4012/Daybreak`(公开,SSH over 443 推送)
    + Gitee 镜像 `https://gitee.com/wergansailikebai/daybreak`(公开,HTTPS + 私人令牌推送)
  - ✅ 发布 CI(`.github/workflows/release.yml`):tag `v*` 触发 → build/test/package → 上传 GitHub Release。
    已实测跑通:CI 5 次全绿、Release 1 次全绿(约 3.5 分钟)
  - ✅ 发布包双通道:`v0.1.1` 同时发布在
    [GitHub Release](https://github.com/gan4012/Daybreak/releases/tag/v0.1.1) 与
    [Gitee 发行版](https://gitee.com/wergansailikebai/daybreak/releases/tag/v0.1.1);
    Gitee 侧用 `scripts/publish-gitee.ps1` 一条命令上传(幂等:已有发行版则更新标题,已有同名附件则跳过)
  - ✅ 自更新器:双源 **Gitee 优先 → GitHub 回退**;`--check / --update`;
    下载支持流式写 .part、Range 断点续传、每源 3 次重试、gh-proxy 镜像回退(仅 github.com 链接)、
    走 config.json 的代理;覆盖前校验 zip 必含主程序 exe。裁剪后 11.5MB
  - ✅ 一键更新:设置页「检查更新」检测到新版本 → 弹窗确认 → 启动 `--update` 并退出主程序 → 自动重启
  - ✅ 微软 ClientId 配置化(设置页可填,留空回退开发期备用 ID)
  - ✅ CI 测试策略:集成测试打 `[Trait("Category","Integration")]`,CI 用 `--filter "Category!=Integration"` 只跑 35 项离线测试
  - ⚠️ 环境注意:GitHub 推送走 SSH `ssh.github.com:443`(github.com:22 与 HTTPS+GCM 均不稳),
    配置在 `~/.ssh/config`;Gitee 令牌存用户级环境变量 `GITEE_TOKEN`
- **M5 剩余**:一键更新完整覆盖重启的实机验证(下载/校验/替换各环节已单独验证)、
  公测分发与反馈迭代、Gitee 附件容量上限确认

## 提交规范

提交信息:类型 + 摘要(中文),正文列要点与测试情况。示例:
`M4 补全:CurseForge 整合包免 key 导入 + Mod 安装记录持久化 + 内测打包脚本`
测试情况必须如实(哪些实测、哪些未验证)。

## 修改代码前的检查清单

1. 读完本文件 + 相关模块源码
2. 改 Core 层先跑 `dotnet test`(勿在启动器运行时构建,文件锁)
3. 改 UI 后必须实机截图验证(tools/capture.ps1),不能只看编译通过
4. 涉及下载/启动流程的改动,必须在控制台(engdbg 模式)或实机完整走一遍
5. 新增第三方依赖先查许可证(MIT/Apache 才行),并更新 THIRD-PARTY-NOTICES.md

### 联机(EasyTier 虚拟组网)相关坑

- **自建中继节点**:<中继地址见 easytier/relay.txt,不进 git>(腾讯云轻量 2核2G,Ubuntu 22.04,systemd 服务名 easytier,
  TCP+UDP 11010,内存约 33MB)。**EasyTier 官方公共节点已全部下线**(实测 5 个候选域名全部 NXDOMAIN),
  网上教程里的 public.easytier.top / easytier.public.kkrainbow.top 都已失效。
- **easytier-core 只能给一个 -e**:传两个(哪怕一个是 tcp 一个是 udp)会让进程**立刻退出且不写任何日志**,
  排查时极易误判为"权限问题"。已由单元测试锁死(EasyTierArgumentsTests)。
- **TUN 必须管理员权限**:非管理员报 	un device error / Failed to create adapter。
  已在联机功能里做**按需提权**(ElevationService):平时普通权限运行,点"创建房间"时才提示重启为管理员,
  避免所有用户每次启动都吃 UAC、也避免游戏进程继承管理员身份。
- **--no-tun 模式无法绑定虚拟 IP**:会被 os error 10049(地址无效) 拒绝,所以不能靠端口转发绕开管理员权限,
  必须走 TUN。端口转发(--port-forward)本身在 Windows 免管理员可用(已实测),但只能用于"连出去"的方向。
- **提权窗口无法被自动化点击**:UIPI 会阻止非提权进程把提权窗口设为前台,tools/click.ps1 / mouse_event 对它无效。
  验证提权后的功能要么手工点,要么用 Start-Process -Verb RunAs 直接以管理员启动。
- **房间号不落明文到网络名**:最初把房间号直接拼进 network-name(dbk-123456),
  这会让中继节点上能看到别人的房间号。现改为用 HMAC 分别派生 name 和 secret(不同标签,防止名字成为密钥前缀)。
- **6 位房间号是"可猜的"**:90 万种组合,猜到就能进房间。这是"手输 6 位数字"的固有代价;
  界面已提示"房间号相当于密码,直播/录屏注意遮挡"。
- **EasyTier 许可证已从 Apache-2.0 改为 LGPL-3.0**(官网页脚仍是旧的 Apache 说法)。
  按更严格的 LGPL-3.0 守法:独立子进程调用、不修改、随包附完整许可证文本,用户可自行替换 exe。

### 好友联机功能验证记录(2026-10-26)

**已完成的端到端验证**(不依赖 UI,直接用两台机器跑通网络层):

| 环节 | 结果 |
|---|---|
| relay 节点 | ✅ 腾讯云 <中继节点地址>, TCP+UDP 11010,systemd 开机自启,33MB 内存 |
| 房主端 TUN | ✅ 提权后创建虚拟网卡 et_*,分配 10.144.144.1 |
| 加入者端 TUN | ✅ --dhcp 自动分配 10.144.144.2 |
| peer 互相发现 | ✅ 双方各发现 2 个 peer(中继 + 对端) |
| **虚拟网络数据传输** | ✅ **加入者连 10.144.144.1:25565 成功,双向收发验证通过** |
| UI | ✅ 四个 Tab 渲染、提权提示与重启流程正常 |
| 单元测试 | ✅ 151 项(新增 33 项房间号/参数构造) |
| 发布包 | ✅ zip 96.6MB,含 easytier 全部 6 个文件 |

验证方法(可复现):本机用 --ipv4 10.144.144.1 起房主 + 监听 0.0.0.0:25565 的测试服务,
服务器用 --dhcp 起加入者,从服务器 python3 连 10.144.144.1:25565,
返回 DAYBREAK_SERVER_HERE:HELLO_FROM_GUEST 即通过。**注意加入者要指定 --listeners tcp://0.0.0.0:12010,
否则会和同机的 relay 抢 11010。**

**未验证**:真实 Minecraft 客户端之间的"建房→好友加入→游戏内进服"完整流程
(需要两个人真机操作,提权窗口无法自动化点击)。网络层已证明可用,剩余是 UI 操作验证。

### v0.1.3 发布记录(2026-10-26)

- GitHub Release: https://github.com/gan4012/Daybreak/releases/tag/v0.1.3(97.47 MB)
- Gitee 发行版: https://gitee.com/wergansailikebai/daybreak/releases/tag/v0.1.3(id=1241194)
- 网盘包:`Daybreak-v0.1.3-win-x64.zip`(96.68 MB，由 scripts/package-netdisk.ps1 生成)
- 更新器实测命中 Gitee，`--check` 返回 UP_TO_DATE

**发版流程里新增的一步(以前没有)**:
CI 现在需要仓库 Secret `DAYBREAK_RELAY`(值为中继地址 host:port)。
workflow 里做了前置校验，缺失会立刻在「Check relay config」步骤失败并给出提示，
而不是等到打包最后才报错。

**踩坑**:
- 第一次发版 CI 失败在 Package 步骤，原因是 relay.txt 被 gitignore 后 CI 拿不到。
  修复为从 Secret 注入，并同时兼容 `vars.DAYBREAK_RELAY`两种配置方式。
- 判断 Gitee 发行版里的中文是否真的乱码，不能看 PowerShell 的 `Invoke-RestMethod` 回显
  (它按错误编码解码)，要 `curl` 存原始字节再用 UTF-8 解。实测服务端存的是正确中文。
