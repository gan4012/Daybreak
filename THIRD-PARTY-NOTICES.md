# 第三方开源组件声明

Daybreak 破晓启动器基于以下开源组件构建。全部为 MIT / Apache-2.0 等宽松许可。

## 直接依赖

| 组件 | 版本 | 用途 | 许可证 | 来源 |
|---|---|---|---|---|
| WPF UI (WPF-UI) | 4.3.0 | Fluent 风格 WPF 控件库 | MIT | https://github.com/lepoco/wpfui |
| CommunityToolkit.Mvvm | 8.4.2 | MVVM 框架(源代码生成器) | MIT | https://github.com/CommunityToolkit/dotnet |
| Microsoft.Extensions.Hosting | 10.0.13 | 依赖注入与宿主 | MIT | https://github.com/dotnet/runtime |
| Serilog | 4.4.0 | 结构化日志 | Apache-2.0 | https://github.com/serilog/serilog |
| Serilog.Extensions.Hosting | 10.0.0 | Serilog 与宿主集成 | Apache-2.0 | https://github.com/serilog/serilog-extensions-hosting |
| Serilog.Sinks.File | 7.0.0 | 日志落盘 | Apache-2.0 | https://github.com/serilog/serilog-sinks-file |
| CmlLib.Core | 4.1.0 | Minecraft 版本解析 / 安装 / 启动协议 | MIT | https://github.com/CmlLib/CmlLib.Core |
| CmlLib.Core.Auth.Microsoft | 3.4.0 | 微软 OAuth → XBL → XSTS → Minecraft 认证链 | MIT | https://github.com/CmlLib/CmlLib.Core.Auth.Microsoft |
| CmlLib.Core.Installer.Forge | 2.0.0 | Forge / NeoForge 安装器(processors 执行链) | MIT | https://github.com/CmlLib/CmlLib.Core.Installer.Forge |
| XboxAuthNet.Game.Msal | 0.2.0 | MSAL 与 Xbox 认证桥接 | MIT | https://github.com/CmlLib/XboxAuthNet |

## 关键传递依赖

| 组件 | 用途 | 许可证 | 来源 |
|---|---|---|---|
| Microsoft.Identity.Client (MSAL.NET) | OAuth 设备码授权 | MIT | https://github.com/AzureAD/microsoft-authentication-library-for-dotnet |
| XboxAuthNet.Game | Xbox Live / XSTS 认证 | MIT | https://github.com/CmlLib/XboxAuthNet |
| System.Text.Json | JSON 序列化(.NET 运行时内置) | MIT | https://github.com/dotnet/runtime |

## 仅测试项目引用(不随发布包分发)

| 组件 | 许可证 |
|---|---|
| xunit | Apache-2.0 |
| xunit.runner.visualstudio | Apache-2.0 |
| Microsoft.NET.Test.Sdk | MIT |
| coverlet.collector | MIT |

## 运行时从官方源下载的组件(不随本项目分发)

| 组件 | 许可证 | 说明 |
|---|---|---|
| authlib-injector | **AGPL-3.0** | 外置登录注入用。本项目**只读取其技术规范自行实现 Yggdrasil 协议**,不包含、不修改其代码;jar 在用户使用外置登录时从官方发布源下载,属独立进程运行的第三方程序。 |
| Minecraft Java Edition 游戏文件 | Mojang EULA | 由启动器在运行时从微软官方源下载,本项目不包含任何游戏本体文件。 |
| Java 运行时(Temurin / Microsoft OpenJDK) | GPL-2.0 with Classpath Exception | 由启动器从 piston-meta 官方源下载,独立运行时环境。 |
| Mod / 资源包 / 整合包 | 各自许可证 | 由用户从 Modrinth / CurseForge 等第三方平台获取。 |

## 说明

- 本项目为**自主实现**:未参考、未复制任何闭源 Minecraft 启动器的代码、UI 资源、图标或文案。
- 所有引用的开源组件均保持原样使用(未修改源码)。
- 完整许可证文本可在对应项目的官方仓库中查阅。
