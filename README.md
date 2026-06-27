# BSL-Station

> 基于 [Starward](https://github.com/Scighost/Starward) 的 Windows 游戏统一管理启动器。

BSL-Station 是一个面向身边人使用的 Windows 原生游戏启动器，在 Starward 成熟基础上扩展，统一管理多款游戏的下载、更新、启动和设置。

## 支持游戏

| 游戏 | 下载 | 更新 | 预下载 | 启动 | 修复 | 卸载 | 导入 | 任务队列 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 原神 | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | Starward 原生 |
| 崩坏：星穹铁道 | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | Starward 原生 |
| 绝区零 | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | Starward 原生 |
| 鸣潮 | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | BSL 自建队列 |
| 明日方舟 | ✓ | ✓ | — | ✓ | ✓ | ✓ | ✓ | BSL 自建队列 |
| 明日方舟：终末地 | ✓ | ✓ | — | ✓ | ✓ | ✓ | ✓ | BSL 自建队列 |

> 明日方舟和终末地的预下载在 V1 中明确不可用。

## 功能特性

- **统一首页**：顶部游戏图标切换，右侧操作区显示真实后端状态
- **任务中心**：紧凑列表布局，区分 Starward 原生任务和 BSL 自建队列任务，支持暂停、继续、取消、重试、移除
- **单任务队列**：同一时间只执行一个下载任务，支持排队、失败重试、断点续传
- **空间检查**：下载前自动检查磁盘空间，估算下载包、解压和缓存所需空间
- **缓存管理**：默认清理普通下载缓存，失败时提示残留缓存并提供手动清理入口
- **游戏设置**：米哈游游戏使用 Starward 原设置页，第三方游戏使用统一 BSL 设置弹窗
- **导入已有游戏**：支持手动指定已安装目录导入
- **自定义启动**：支持自定义启动程序和启动参数

## 技术栈

- **框架**：.NET 10 + WinUI 3 (Windows App SDK 1.8)
- **UI 框架**：CommunityToolkit.WinUI、Win2D
- **数据**：SQLite + Dapper
- **日志**：Serilog
- **目标平台**：Windows 10 1809 (17763) 及以上
- **支持架构**：x64、ARM64、x86

## 项目结构

```
BSL-Station/
├── BSL-Station-Starward/          # 主工程
│   ├── src/
│   │   ├── Starward/              # 主 WinUI 应用
│   │   ├── Starward.Core/         # 核心库（游戏记录、HoYoPlay API 等）
│   │   ├── Starward.Language/     # 多语言资源
│   │   ├── Starward.Launcher/     # C++ 启动器
│   │   ├── Starward.RPC/          # RPC 服务
│   │   ├── Starward.Setup/        # 安装程序
│   │   └── Starward.Setup.Core/   # 安装程序核心库
│   └── Starward.slnx              # 解决方案文件
├── design/                        # UI 设计稿
│   └── task-page/                 # 任务中心 HTML 设计稿
├── refs/                          # 参考仓库
├── REQUIREMENTS.md                # V1 需求文档
├── PROGRESS.md                    # 开发进度
├── UI-ADAPTATION.md               # UI 适配进度
├── UI-PLANNING.md                 # UI 规划
└── BSL-TESTING.md                 # 测试验证矩阵
```

## 构建

### 环境要求

- Visual Studio 2022
  - .NET 桌面开发工作负载
  - C++ 桌面开发工作负载
  - Windows 应用程序开发工作负载
- .NET 10 SDK

### 构建命令

```powershell
# 构建主工程（x64）
dotnet build BSL-Station-Starward/src/Starward/Starward.csproj -p:Platform=x64

# 发布
dotnet publish BSL-Station-Starward/src/Starward -c Release -r win-x64 -p:Platform=x64
```

### 运行

构建产物位于 `BSL-Station-Starward/src/Starward/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/`，运行 `BSL-Station.exe` 即可。

## 分游戏适配说明

### 原神 / 崩坏：星穹铁道 / 绝区零

直接复用 Starward 原生下载、更新、预下载、修复、启动链路，使用 Starward 原游戏设置页。

### 鸣潮

通过 BSL 自建下载队列适配，支持完整的游戏生命周期管理，包括下载、更新、预下载、修复、导入、启动、卸载和缓存管理。

### 明日方舟 / 终末地

基于 Hypergryph 官方接口适配，通过 BSL 自建下载队列管理。支持下载、更新、修复、导入、启动、卸载。V1 不支持预下载。

## 文档

- [V1 需求文档](./REQUIREMENTS.md)
- [开发进度](./PROGRESS.md)
- [UI 适配进度](./UI-ADAPTATION.md)
- [UI 规划](./UI-PLANNING.md)
- [测试验证矩阵](./BSL-TESTING.md)

## 致谢

- [Starward](https://github.com/Scighost/Starward) — 主工程基础
- [Collapse](https://github.com/neon-nyan/Collapse) — 设计灵感来源
- [Snap Hutao](https://github.com/DGP-Studio/Snap.Hutao) — 技术参考
- [Hi3Helper.Plugin.Hypergryph](https://github.com/CollapseLauncher/Collapse/Hi3Helper.Plugin.Hypergryph) — Hypergryph 接口参考

## 许可证

[MIT License](./BSL-Station-Starward/LICENSE)
