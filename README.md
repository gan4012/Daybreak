# Daybreak 破晓启动器

一个自主品牌的 Minecraft Java 版启动器,仅支持 Windows。C# / .NET 10 + WPF。
代码、UI、品牌全部自主实现——基于 MIT/Apache 许可的开源库构建,未参考任何闭源启动器代码。

## 免责声明

本项目与 Mojang Studios 及 Microsoft 无关。Minecraft 是 Mojang Synergies AB 的商标。
本软件免费提供,不含任何游戏本体文件——游戏资源由启动器在运行时从官方源下载。

## 功能

### 登录(三合一)
- **离线登录** — 仅限单机与测试,联机请使用正版
- **微软正版** — MSAL 设备码授权,令牌缓存与静默续期
- **外置登录** — Yggdrasil 协议(如 LittleSkin),自动注入 authlib-injector

### 游戏安装与启动
- 版本清单拉取,官方源 / BMCLAPI 镜像双源可切
- 自研多线程下载引擎:Range 分片并发、SHA1 校验、镜像自动降级
- Java 运行时自动匹配与下载(读版本 JSON 的 javaVersion 组件,避免版本不匹配秒退)
- **版本隔离**:各版本独立的存档 / 配置 / mods 目录(libraries/assets 仍共享)
- 版本管理:重命名、复制、删除、打开目录、Mod 清理

### Mod 生态
- 加载器安装:Fabric / Forge / NeoForge
- **Modrinth 资源市场**:搜索、按游戏版本与加载器过滤、一键安装
- 整合包导入:`.mrpack`(Modrinth 格式)与 CurseForge zip(免 API key)
- Mod 更新检查

### 诊断与体验
- **崩溃诊断**:扫描 crash-reports 与 latest.log,12 条规则给出人话建议
- 主题:深色 / 浅色 / 跟随系统 + Acrylic / Mica / 无特效
- 自定义 JVM 参数、内存分配、HTTP / SOCKS5 代理
- 实时下载速度、窗口状态记忆
- 内置更新器(从 GitHub Releases 检查与安装新版本)

## 下载

前往 [Releases](https://github.com/gan4012/Daybreak/releases) 下载最新内测包:

- `Daybreak-内测包-win-x64.zip` — 自包含单文件,**无需安装 .NET 运行时**
- 解压到任意目录,双击 `Daybreak.Launcher.exe` 即可

首次启动游戏会自动下载匹配的 Java 运行时与游戏资源(数百 MB,取决于版本),请耐心等待。

## 从源码构建

环境要求:.NET 10 SDK、Windows 10 1809+。

```powershell
dotnet build Daybreak.slnx -c Debug

# 全部测试(43 项 = 35 离线单元 + 8 联网集成,集成测试约 5 分钟)
dotnet test Daybreak.slnx -c Debug

# 只跑离线单元测试(CI 用这个)
dotnet test Daybreak.slnx -c Debug --filter "Category!=Integration"

# 运行
cd Daybreak.Launcher/bin/Debug/net10.0-windows; ./Daybreak.Launcher.exe

# 打包内测 zip(自包含单文件 + 更新器 + 使用说明)
powershell -File scripts/package.ps1
```

## 项目结构

| 项目 | 职责 |
|---|---|
| `Daybreak.Launcher` | WPF 主程序(Views / ViewModels / Services / Themes) |
| `Daybreak.Core` | 启动核心:Game / Auth / Mod / Market / Diagnostics / Storage,不依赖任何 WPF 类型 |
| `Daybreak.Market` | 资源市场客户端(预留) |
| `Daybreak.Updater` | 独立更新器:检查并从 Releases 安装新版本 |
| `Daybreak.Core.Tests` | xUnit(离线单元测试 + 联网集成测试) |

## 里程碑

- [x] **M0** 项目奠基:解决方案骨架、WPF UI 框架、品牌色板、CI
- [x] **M1** 能启动游戏:版本下载、离线登录、启动参数、进程拉起
- [x] **M2** 正版与外置登录:微软 OAuth、Yggdrasil、Java 自动管理
- [x] **M3** Mod 生态:Fabric/Forge/NeoForge、自研下载引擎、版本隔离
- [x] **M4** 资源市场:Modrinth 接入、一键安装、整合包导入
- [ ] **M5** 打磨与公测:更新器与发布 CI 已完成,公测分发进行中

## 已知限制

- 仅支持 Windows 10 1809+
- 内测阶段功能与接口可能变动
- Forge / NeoForge 安装链路已实现,但未覆盖所有历史版本组合

## 反馈

- 提交 [Issue](https://github.com/gan4012/Daybreak/issues)
- 反馈问题时请附上 `%APPDATA%\Daybreak\logs` 目录下的日志

## 开源组件

WPF UI (MIT) · CommunityToolkit.Mvvm (MIT) · CmlLib.Core (MIT) · Serilog (Apache-2.0)
详见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

## 许可证

[MIT](LICENSE)
