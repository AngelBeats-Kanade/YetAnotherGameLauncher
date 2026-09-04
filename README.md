# YetAnotherGameLauncher

一个基于 **.NET 10 + Avalonia 12 + MVVM** 的开源游戏启动器，为 Linux（及 Windows）桌面设计。
游戏本体通过 **配置文件** 驱动，代码不内置任何具体游戏；当前目标是：

- 《鸣潮》（库洛官方启动器协议，支持 国服 / B服 / 国际服）
- 《明日方舟：终末地》（GRYPHLINE 启动器协议，国际服）

> [!NOTE]
> 《鸣潮》《明日方舟：终末地》均为 Windows 程序，在 Linux 上运行依赖 **wine / Proton**。
> 本启动器不代管 wine，而是通过启动命令模板（见 [GAME_CONFIG.md](docs/GAME_CONFIG.md)）支持任意包装方式。

## 功能

| 功能 | 说明 |
|---|---|
| 游戏启动 | 命令模板 `{exe}` / `{installDir}` 占位符 + 环境变量注入，支持 `wine {exe}`、Proton、`steam -applaunch` 等 |
| 全量下载 | 官方清单逐文件同步（鸣潮）/ 压缩包整包解压（终末地），size+MD5 双校验 |
| 断点续传 | `.temp` 临时文件 + HTTP Range 续传，瞬态网络错误线性退避重试 |
| 增量更新 | 鸣潮：匹配官方 `patchConfig` 差分入口，下载 krpdiff 差分包，调用原生 `hpatchz`（HDiffPatch）合成，`.yagl-bak` 备份回滚 |
| 预更新（预下载） | 两段式：先"预下载"暂存到 `.yagl/predownload`，官方开放后一键"应用"；鸣潮走差分包、终末地走整包 |
| 校验修复 | 按清单事后校验（MD5），自动修复缺失/损坏文件，清理游离文件（保留 `Saved/` 存档） |
| 多服务器 | 鸣潮国服/B服/国际服 一键切换（配置驱动） |
| 现代化 UI | Avalonia FluentTheme，亮/暗/跟随系统三态主题，卡片式布局，中文界面 |
| 架构 | 前后端分离：`Core`（领域层）→ `Channels.*`（厂商渠道）→ `App`（Avalonia UI），全部依赖抽象接口 |

## 快速开始

### 环境要求

- .NET 10 SDK（开发/构建）；运行 self-contained 发布产物则**无需安装运行时**
- Linux 上运行游戏：自备 wine / Proton（例如 `wine`、`steam`、`umu-launcher`）
- 鸣潮增量更新：需要 `hpatchz`（HDiffPatch）可执行文件，默认从 PATH 解析，
  也可在配置中指定路径（见下文）

### 构建与运行（开发）

```bash
git clone <本仓库>
cd YetAnotherGameLauncher
dotnet run --project src/YetAnotherGameLauncher
```

### 发布产物

```bash
# Linux（self-contained，可执行文件在 publish/linux-x64/）
dotnet publish src/YetAnotherGameLauncher -c Release -r linux-x64 --self-contained -o publish/linux-x64

# Windows
dotnet publish src/YetAnotherGameLauncher -c Release -r win-x64 --self-contained -o publish/win-x64
```

### 运行测试（154 个）

```bash
dotnet test
```

测试覆盖：配置解析/校验、下载器（续传/重试/MD5）、清单校验、版本计划、
全量同步、增量应用（含回滚）、包式安装、更新编排、渠道解析（鸣潮/终末地）、
启动命令解析、主题切换、ViewModel 状态机、以及 Avalonia.Headless 真实窗口集成测试。

## 配置

配置文件位置：

- Linux：`~/.config/yagl/games.json`
- Windows：`%APPDATA%\yagl\games.json`
- 可用环境变量 `YAGL_CONFIG` 覆盖

首次运行若配置缺失，启动器会提示并引导创建；也可直接复制
[`samples/games.json`](samples/games.json)（已内置鸣潮三服与终末地国际服的官方端点）。
完整字段说明与"添加新游戏"教程见 [docs/GAME_CONFIG.md](docs/GAME_CONFIG.md)。

### Linux 上通过 wine/Proton 启动的配置示例

```jsonc
{
  "id": "wuthering-waves",
  "displayName": "鸣潮",
  "channel": "kuro",
  "installDir": "WutheringWaves",
  "executable": "Client/Binaries/Win64/Client-Win64-Shipping.exe",
  "launch": {
    "commandTemplate": "wine {exe}",                 // 或 "steam -applaunch 0" 等
    "environment": { "WINEPREFIX": "~/Games/wuwa-prefix", "DXVK_HUD": "0" }
  }
}
```

## 文档

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) —— 架构总览、模块依赖图、全部核心流程图（mermaid）
- [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) —— 开发说明：环境、TDD 工作流、测试布局、如何新增渠道/游戏
- [docs/GAME_CONFIG.md](docs/GAME_CONFIG.md) —— games.json 配置参考与教程
- [PLAN.md](PLAN.md) —— 立项计划与开发里程碑（已全部完成）

## 目录结构

```
YetAnotherGameLauncher.slnx
├── Directory.Build.props / Directory.Packages.props   # 中央包管理
├── global.json                                        # 启用 dotnet test 的 MTP 模式
├── src/
│   ├── YetAnotherGameLauncher.Core/                   # 领域层（下载/清单/同步/增量/包式/编排/启动）
│   ├── YetAnotherGameLauncher.Channels.Kuro/          # 库洛渠道（鸣潮）+ hpatchz 补丁器
│   ├── YetAnotherGameLauncher.Channels.Hypergryph/    # GRYPHLINE 渠道（终末地）
│   └── YetAnotherGameLauncher/                        # Avalonia UI（MVVM）
├── tests/                                             # xunit.v3（MTP）+ Avalonia.Headless
├── samples/games.json                                 # 示例配置
└── docs/                                              # 开发/架构/配置文档
```

## 故障排查

| 现象 | 处理 |
|---|---|
| 启动器提示"未找到配置文件" | 复制 `samples/games.json` 到 `~/.config/yagl/games.json`，或用环境变量 `YAGL_CONFIG` 指定路径 |
| 提示"无法连接服务器" | 检查网络/代理；鸣潮 CDN 在国内网络环境更稳定 |
| 鸣潮增量更新失败，提示 hpatchz | 安装 [HDiffPatch](https://github.com/sisong/HDiffPatch/releases) 并确保 `hpatchz` 在 PATH 中 |
| 预下载按钮不出现 | 官方未开放预下载窗口（鸣潮 `predownload.config` 不存在 / 终末地无 `patch` 节点） |
| 终末地版本/下载报错 | GRYPHLINE 协议无官方文档，官方启动器更新后字段可能变化，欢迎提 issue |
| 游戏点"启动"无反应 | Linux 上需配置 `launch.commandTemplate`（如 `wine {exe}`）；Windows 直接 `{exe}` 即可 |

## 许可与致谢

- 下载/更新/预更新流程参考并致敬 [timetetng/wutheringwaves-cli-manager](https://github.com/timetetng/wutheringwaves-cli-manager)（鸣潮协议逆向）
- 终末地协议参考 [LLauncher](https://github.com/AugustLigh/LLauncher) 与 [ak-endfield-api-archive](https://github.com/daydreamer-json/ak-endfield-api-archive)
- 补丁工具为 [HDiffPatch](https://github.com/sisong/HDiffPatch)（MIT）
- 本项目为社区工具，与库洛游戏、鹰角网络无任何关联
