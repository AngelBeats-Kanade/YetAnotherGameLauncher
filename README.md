# YetAnotherGameLauncher

[![CI](https://github.com/AngelBeats-Kanade/YetAnotherGameLauncher/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/AngelBeats-Kanade/YetAnotherGameLauncher/actions/workflows/ci.yml)

一个基于 **.NET 10 + Avalonia 12 + MVVM** 的开源游戏启动器，为 Linux（及 Windows）桌面设计。
游戏本体通过 **配置文件** 驱动，代码不内置任何具体游戏；当前目标是：

- 《鸣潮》（库洛官方启动器协议，支持 国服 / B服 / 国际服）
- 《明日方舟：终末地》（GRYPHLINE / 鹰角启动器协议，支持 国际服 / 国服 / B服）

> [!NOTE]
> 《鸣潮》《明日方舟：终末地》均为 Windows 程序，在 Linux 上运行依赖 **wine / Proton**。
> 本启动器不代管 wine，而是通过启动命令模板（见 [GAME_CONFIG.md](docs/GAME_CONFIG.md)）支持任意包装方式。

## 功能

| 功能 | 说明 |
|---|---|
| 游戏启动 | 命令模板 `{exe}` / `{installDir}` 占位符 + 环境变量注入；启动预检（主程序/运行时/prefix）给出类目化中文错误，游戏输出落盘启动日志（`~/.local/share/yagl/logs/`），失败弹主题化错误卡（含打开日志目录） |
| Linux 启动方式选择器 | Direct / 原生 umu / umu-launcher / Wine / Proton 五选（+自定义模板）；默认走**原生 umu**（内置 C# 启动链，按 Proton 的 toolmanifest 自动下载 GE/UMU-Proton 与 Steam Runtime 并搭建容器与 prefix，无需外部 umu-run），也可自动发现 umu-run、系统 wine、Lutris runner 与 GE-Proton 改走外部链路，检测到 NVIDIA GPU 时推荐兼容环境；外部 umu-run 未装提供一键安装（GitHub release zipapp，装完即用）；Linux 首运自动把默认 `{exe}` 模板升级为推荐链（原生 umu → umu-run → Proton → wine）并落盘，开箱即可点启动 |
| 全量下载 | 官方清单逐文件同步（鸣潮）/ 压缩包整包解压（终末地），size+MD5 双校验 |
| 断点续传 | `.temp` 临时文件 + HTTP Range 续传，瞬态网络错误线性退避重试 |
| 下载限速 | 可按字节/秒限制下载速度 |
| 增量更新 | 鸣潮：匹配官方 `patchConfig` 差分入口，下载 krpdiff 差分包，调用原生 `hpatchz`（HDiffPatch）合成，`.yagl-bak` 备份回滚 |
| 预更新（预下载） | 两段式：先“预下载”暂存到 `.yagl/predownload`，官方开放后一键“应用”；鸣潮走差分包、终末地走整包 |
| 登记版本 | 包式渠道检测到本机已安装游戏文件时零下载直接登记（终末地检测态） |
| 校验修复 | 按清单事后校验（MD5），自动修复缺失/损坏文件，清理游离文件（保留 `Saved/` 存档） |
| 多服务器 | 鸣潮国服/B服/国际服、终末地国际服/国服/B服 一键切换（全部配置驱动） |
| 唤取（抽卡）记录 | 鸣潮：游戏内地址自动提取、官方接口拉取、本地缓存与保底统计 |
| 现代化 UI | 圆角无边框窗口 + 自绘标题栏（拖拽区 / 最小化 / 最大化 / 关闭）；海报式详情页：当期海报全幅铺满主区域（左缘完整不裁切、无遮罩）+ 左上圆角全出血布局；侧栏选中指示点两段式动效；窗口宽度穿越阈值侧栏自动收放（带滞回防抖）；亮/暗/跟随系统三态主题；页面切换与按钮微动效；设置页可自定义应用背景 |
| 背景视频 | 详情页播放官方当期背景视频：FFmpeg 硬解（Windows D3D11VA / Linux VAAPI→NVDEC），智能循环点 + 预卷零间隙续播（循环无缝）；Linux 上优先复用发行版 FFmpeg 9（libavcodec.so.63），缺失时自动下载 BtbN 构建到应用数据目录 |
| Wine prefix | 统一放在应用数据目录 `~/.local/share/yagl/prefixes/<游戏id>`（Windows 形态的 STEAM_COMPAT_DATA_PATH 同样指向此处），绝不写入游戏安装目录——安装同步不会误删 |
| 界面语言 | 简体中文 / English，跟随系统可选，切换即时生效（设置页调整） |
| 代理设置 | 跟随系统 / 直连 / 手动三选 |
| 开机自启动 | Windows 注册表 / Linux XDG autostart |
| 启动设置 | 独立的游戏设置次页（从详情页齿轮进入）：位置 / 启动方式 / 启动参数，保存回配置文件 |
| 存储路径可视化配置 | 设置页可改安装根目录；游戏设置页“位置”内可单独修改每个游戏的安装目录，保存即时生效 |
| 官方图标 | 鸣潮/终末地使用官方应用图标（默认内置资源 `avares://YetAnotherGameLauncher/Assets/game-icons/*.jpg`，`icon` 字段仍支持 URL/本地路径，加载失败回退首字） |
| 架构 | 前后端分离：`Core`（领域层）→ `Channels.*`（厂商渠道）→ `App`（Avalonia UI），全部依赖抽象接口 |

## 快速开始

### 环境要求

- .NET 10 SDK（开发/构建）；运行 self-contained 发布产物则**无需安装运行时**
- Linux 上运行游戏：默认无需任何外部运行时——原生 umu 启动链会按需自动下载
  **UMU-Proton 与 Steam Runtime**（启动设置卡可一键预下载/检查；组件下载失败可从错误卡重试或改选本机 Proton）；
  也可自备 wine / Proton / umu-launcher（umu 未装时启动设置卡与错误提示内可一键安装）
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

### 运行测试（438 个，2026-09 实测）

```bash
# 4 个测试工程分别运行编译产物（Windows 亦可直接跑 .exe；本机 dotnet test 可能发现 0 个测试）：
dotnet tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll
dotnet tests/YetAnotherGameLauncher.Channels.Kuro.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Channels.Kuro.Tests.dll
dotnet tests/YetAnotherGameLauncher.Channels.Hypergryph.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Channels.Hypergryph.Tests.dll
dotnet tests/YetAnotherGameLauncher.App.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.App.Tests.dll
```

测试覆盖：配置解析/校验、下载器（续传/重试/MD5）、清单校验、版本计划、
全量同步、增量应用（含回滚）、包式安装、更新编排、渠道解析（鸣潮/终末地国服/国际服参数）、
GPU 厂商探测、Wine 运行时发现（umu/wine/Lutris）与推荐链、Wine prefix 统一路径、
启动预检与类目化错误、启动日志落盘、umu-launcher 引导安装、启动命令解析、
本地化服务与语言切换、侧栏折叠/页面切换/关于页、主题切换、
指示点几何落位与迁移编舞、详情页布局状态、玻璃按钮四态前景、
启动失败覆盖层、ViewModel 状态机、以及 Avalonia.Headless 真实窗口集成测试。
另附视觉自检截图工具（`artifacts/ui-review/`，见 docs/DEVELOPMENT.md）。

## 配置

配置文件位置：

- Linux：`~/.config/yagl/games.json`
- Windows：`%APPDATA%\yagl\games.json`
- 可用环境变量 `YAGL_CONFIG` 覆盖

首次运行若配置缺失，启动器会**自动在默认位置生成默认配置文件**
（内容即 [`samples/games.json`](samples/games.json) 模板：内置鸣潮三服与终末地三服
（国际/国服/B服）及官方图标，开箱即可下载；已存在时绝不覆盖）。
Linux 上生成时会顺带把默认 `{exe}` 模板升级为社区推荐链
（原生 umu → umu-run → Proton → wine；默认原生 umu 无需任何外部运行时，
启动时自动下载 Proton 与 Steam Runtime）并写入兼容环境变量；
此升级只在首运生成时发生一次，用户此后的任何修改都不会被覆盖。

**从旧版本升级**：旧配置首次被新版加载时会自动迁移——按内置模板补齐同一游戏新增的
官方服务器与本地化名称（`schemaVersion` 一次性写入，仅执行一次），状态栏会提示补了什么。
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
├── tools/IconGen/                                     # 应用图标生成工具（原创二次元形象，headless 渲染 → 多尺寸 ICO）
├── tests/                                             # xunit.v3（MTP）+ Avalonia.Headless
├── samples/games.json                                 # 示例配置
└── docs/                                              # 开发/架构/配置文档
```

## 故障排查

| 现象 | 处理 |
|---|---|
| 首次启动想重新生成默认配置 | 删除 `~/.config/yagl/games.json`（或 `YAGL_CONFIG` 指向的文件）后重启启动器即可 |
| 提示"无法连接服务器" | 检查网络/代理；鸣潮 CDN 在国内网络环境更稳定 |
| 鸣潮增量更新失败，提示 hpatchz | 安装 [HDiffPatch](https://github.com/sisong/HDiffPatch/releases) 并确保 `hpatchz` 在 PATH 中 |
| 预下载按钮不出现 | 官方未开放预下载窗口（鸣潮 `predownload.config` 不存在 / 终末地无 `patch` 节点） |
| 终末地版本/下载报错 | GRYPHLINE 协议无官方文档，官方启动器更新后字段可能变化，欢迎提 issue |
| Linux 启动失败 | 启动失败会弹出错误卡：原生 umu 组件下载失败可重试或改选本机已装 Proton；外部 umu-run 未装可一键安装；也可"打开日志目录"查看 `launch-*.log`，或到游戏设置页检查命令模板与运行时。Windows 直接 `{exe}` 即可 |
| Linux 下界面小/发糊 | 启动器启动时会自动把 Hyprland 缩放同步到 `Xft.dpi`（仅在未设置时写入）；手动方案 `xrdb -merge <<< "Xft.dpi: 160"`（数值 = 96 × 合成器缩放） |
| 鸣潮背景不显示 | 官方 `switch.json` 当前未投放背景时属正常（本机有官方启动器缓存/历史投放会自动回退显示）；视频背景首次播放需联网下载 FFmpeg 库（约 50MB，失败时保留静态海报） |

## 许可与致谢

- 下载/更新/预更新流程与背景配置协议（switch.json / 启动器缓存）参考并致敬 [timetetng/wutheringwaves-cli-manager](https://github.com/timetetng/wutheringwaves-cli-manager)（鸣潮协议逆向）
- 终末地协议参考 [LLauncher](https://github.com/AugustLigh/LLauncher) 与 [ak-endfield-api-archive](https://github.com/daydreamer-json/ak-endfield-api-archive)
- 补丁工具为 [HDiffPatch](https://github.com/sisong/HDiffPatch)（MIT）
- 背景视频解码基于 [FFmpeg](https://ffmpeg.org)（LGPL-2.1+，LGPL 共享构建，经 [FFmpeg.AutoGen](https://github.com/Ruslan-B/FFmpeg.AutoGen) 绑定），原生库缓存放置 `ffmpeg-Builds LGPL` 构建并在校验后解压；归档解压用 [SharpCompress](https://github.com/adamhathcock/sharpcompress)（MIT）
- 本项目为社区工具，与库洛游戏、鹰角网络无任何关联
