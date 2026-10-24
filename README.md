# Daybreak 破晓启动器

一个自主品牌的 Minecraft Java 版启动器(仅 Windows,C#/.NET 10 + WPF)。
本分支追踪 [M0 里程碑:项目奠基](../launcher-plan/计划.md) 的开发进度。

## 免责声明

本项目与 Mojang Studios 及 Microsoft 无关。Minecraft 是 Mojang Synergies AB 的商标。

## 开发

```bash
dotnet build Daybreak.slnx
dotnet test Daybreak.slnx
dotnet run --project Daybreak.Launcher
```

环境要求:.NET 10 SDK、Windows 10 1809+。

## 项目结构

| 项目 | 职责 |
|---|---|
| `Daybreak.Launcher` | WPF 主程序(UI + ViewModel) |
| `Daybreak.Core` | 启动核心(登录 / 下载 / Java / 启动参数),不依赖 UI |
| `Daybreak.Market` | 资源市场(Modrinth / CurseForge 客户端) |
| `Daybreak.Updater` | 独立更新器(自我替换) |
| `Daybreak.Core.Tests` | 核心层单元测试 |

## 里程碑

- [x] **M0** 项目奠基:解决方案骨架、WPF UI 框架、品牌色板、CI
- [ ] **M1** 能启动游戏:版本下载、离线登录、启动参数、进程拉起
- [ ] **M2** 正版与外置登录:微软 OAuth、Yggdrasil、Java 自动管理
- [ ] **M3** Mod 生态:四加载器安装、自研下载引擎、版本隔离
- [ ] **M4** 资源市场:Modrinth 接入、一键安装
- [ ] **M5** 打磨与公测:更新器、崩溃诊断、内测发布

## 开源组件

WPF UI (MIT) · CommunityToolkit.Mvvm (MIT) · CmlLib.Core (MIT) · Serilog (Apache-2.0)
详见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
