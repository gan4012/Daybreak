# Daybreak 破晓启动器

一个自主品牌的 Minecraft Java 版启动器，仅支持 Windows。C# / .NET 10 + WPF。
代码、UI、品牌全部自主实现——基于 MIT/Apache 许可的开源库构建，未参考任何闭源启动器代码。

## 免责声明

本项目与 Mojang Studios 及 Microsoft 无关。Minecraft 是 Mojang Synergies AB 的商标。
本软件免费提供，不含任何游戏本体文件——游戏资源由启动器在运行时从官方源下载。

## 功能

### 登录（三合一）
- **离线登录** — 仅限单机与测试，联机请使用正版
- **微软正版** — MSAL 设备码授权，令牌缓存与静默续期
- **外置登录** — Yggdrasil 协议（如 LittleSkin），自动注入 authlib-injector

### 游戏安装与启动
- 版本清单拉取，官方源 / BMCLAPI 镜像双源可切
- 自研多线程下载引擎：Range 分片并发、SHA1 校验、镜像自动降级
- Java 运行时自动匹配与下载（读版本 JSON 的 javaVersion 组件，避免版本不匹配秒退）
- **版本隔离**：各版本独立的存档 / 配置 / mods 目录（libraries/assets 仍共享）
- 版本管理：重命名、复制、删除、打开目录、Mod 清理
- **首次启动引导**：三步向导配置游戏目录与内存，能识别已有的 `.minecraft` 并复用

### Mod 生态
- 加载器安装：Fabric / Forge / NeoForge
- **Modrinth 资源市场**：搜索、按游戏版本与加载器过滤、一键安装
- 整合包导入：`.mrpack`（Modrinth 格式）与 CurseForge zip（免 API key）
- Mod 更新检查：按文件名搜索、支持名称 / 大小 / 更新状态排序、一键升级可更新项

### 联机

**一键开服** —— 内置四种服务端，选好版本点一下就能开：
- Vanilla、Fabric、Paper、Forge（Forge 1.17+ 的 `@` 参数启动方式已适配）
- 服务端实例独立存放，`server.properties` 可视化编辑（保留注释与未知键）
- 控制台直接发 `op` / `list` / `whitelist` 等命令，停服走 `stop` 保证世界落盘
- EULA 需用户显式确认后才启动

**好友联机** —— 不用公网 IP、不用折腾端口映射，跨网络也能连：
- 点「创建房间」生成 **6 位房间号**，好友在他的启动器里输入房间号即可加入
- 基于 P2P 虚拟组网，自动尝试直连（NAT 打洞），打不通才走中继
- 房间内显示连接状态、已连接好友数、当前游戏版本
- **一键进服**：直接带 `--server` 参数启动游戏，不用手动输地址
- 版本一致性提示：双方游戏版本与 Mod 必须完全一致
- 内置联机日志面板，出问题能直接看到原因

**服务器列表与局域网**：
- 收藏服务器、查询在线人数与延迟、一键进服
- 自动处理 DNS **SRV 记录**（修复了 `2b2t.org` 这类“真实入口在 SRV 里”的服务器直连必超时）
- 监听局域网广播（`224.0.2.60:4445`），自动发现同网络下别人开放的世界

### 诊断与体验
- **崩溃诊断**：扫描 crash-reports 与 latest.log，12 条规则给出人话建议
- 主题：深色 / 浅色 / 跟随系统 + Acrylic / Mica / 无特效
- 自定义 JVM 参数、内存分配、HTTP / SOCKS5 代理
- 实时下载速度、窗口状态记忆、后台安装进度提示
- 内置更新器（Gitee 优先、GitHub 回退）

## 下载

**国内用户推荐走 Gitee**（直连快，无需代理）：

- 🇨🇳 [Gitee 发行版](https://gitee.com/wergansailikebai/daybreak/releases) — 国内直连，实测约 1.5 MB/s
- 🌐 [GitHub Releases](https://github.com/gan4012/Daybreak/releases) — 海外或已有代理时使用

下载 `Daybreak-beta-win-x64.zip`：

- 自包含单文件，**无需安装 .NET 运行时**
- 解压到任意目录，双击 `Daybreak.Launcher.exe` 即可
- 目录里的 `easytier/` 是联机组件，**请勿删除**，否则联机功能不可用

首次启动游戏会自动下载匹配的 Java 运行时与游戏资源（数百 MB，取决于版本），请耐心等待。

启动器内置更新器：**优先从 Gitee 检查与下载更新**，Gitee 不可用时自动回退 GitHub。

## 从源码构建

环境要求：.NET 10 SDK、Windows 10 1809+。

```powershell
dotnet build Daybreak.slnx -c Debug

# 全部测试（176 项 = 154 离线单元 + 22 联网集成，集成测试会真下载游戏与 Java，较慢）
dotnet test Daybreak.slnx -c Debug

# 只跑离线单元测试（CI 用这个，约 2 秒）
dotnet test Daybreak.slnx -c Debug --filter "Category!=Integration"

# 运行
cd src/Daybreak.Launcher/bin/Debug/net10.0-windows; ./Daybreak.Launcher.exe

# 打包内测 zip（自包含单文件 + 更新器 + 联机组件 + 使用说明）
powershell -File scripts/package.ps1
```

> 打包脚本会自动检查 `third_party/easytier/` 下的联机组件（二进制不进 git，缺了会自动下载）
> 以及 `relay.txt`（中继节点地址，不进 git，需本地创建）。

## 项目结构

| 项目 | 职责 |
|---|---|
| `Daybreak.Launcher` | WPF 主程序（Views / ViewModels / Services / Themes） |
| `Daybreak.Core` | 启动核心：Game / Auth / Mod / Market / Server / Net / Diagnostics / Storage，不依赖任何 WPF 类型 |
| `Daybreak.Market` | 资源市场客户端（预留） |
| `Daybreak.Updater` | 独立更新器：检查并从 Releases 安装新版本 |
| `Daybreak.Core.Tests` | xUnit（154 项离线单元测试 + 22 项联网集成测试） |

## 里程碑

- [x] **M0** 项目奠基：解决方案骨架、WPF UI 框架、品牌色板、CI
- [x] **M1** 能启动游戏：版本下载、离线登录、启动参数、进程拉起
- [x] **M2** 正版与外置登录：微软 OAuth、Yggdrasil、Java 自动管理
- [x] **M3** Mod 生态：Fabric/Forge/NeoForge、自研下载引擎、版本隔离
- [x] **M4** 资源市场：Modrinth 接入、一键安装、整合包导入
- [x] **M5** 打磨与公测：更新器、发布 CI、双通道发行
- [x] **M6** 联机：四种服务端一键开服、好友联机房间、服务器列表、局域网发现

## 已知限制

- 仅支持 Windows 10 1809+
- 内测阶段功能与接口可能变动
- **好友联机需要管理员权限**：创建虚拟网卡必须提权，点联机功能时会提示重启为管理员。
  平时不启用联机则不需要管理员权限。
- **好友联机要求双方版本与 Mod 完全一致**，这是 Minecraft 本身的限制
- **6 位房间号是可猜的**（约 90 万种组合）：相当于一个弱密码，请只告诉自己人，
  直播 / 录屏时注意遮挡
- **Forge / NeoForge 未覆盖所有历史版本组合**
- **NeoForge 在国内网络下默认装不了**：上游 Maven 仓库（`maven.neoforged.net`）直连超时，
  且 CmlLib 的 NeoForge 安装器无法注入 HttpClient，镜像改写对它无效。Forge 不受影响。
  临时方案：配置代理后再装

## 反馈

- 提交 [Issue](https://github.com/gan4012/Daybreak/issues)
- 反馈问题时请附上 `%APPDATA%\Daybreak\logs` 目录下的日志
- 联机问题请一并附上联机页里的「联机日志」

## 开源组件

WPF UI (MIT) · CommunityToolkit.Mvvm (MIT) · CmlLib.Core (MIT) · Serilog (Apache-2.0)
· **EasyTier (LGPL-3.0)** — 好友联机的虚拟组网组件，以独立子进程方式调用，不修改其源码

详见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

## 许可证

[MIT](LICENSE)

---

> **关于仓库范围**
> 本仓库只包含**启动器本体**（`src/` 代码 + 构建脚本 + 文档）。
> 官网（`website/`）和演示视频（`video/`）只在本地维护，不同步到这里。
