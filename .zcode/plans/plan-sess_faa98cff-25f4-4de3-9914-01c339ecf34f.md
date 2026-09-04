# YetAnotherGameLauncher 开发计划

> 批准后第一步：将本计划保存为项目根目录 `PLAN.md`，随后按序实施。

## 一、调研结论（已完成）

**本机环境**：.NET 10 SDK 10.0.400（最新 LTS，支持至 2028）；目录中已有 Rider 生成的 Avalonia 模板项目（Avalonia 12.1.2 + CommunityToolkit.Mvvm 8.4.2），非 git 仓库。开发在 Windows 进行，目标运行平台为 Linux（代码全跨平台，Linux 打包用 self-contained publish）。

**鸣潮（参考仓库 wutheringwaves-cli-manager，已通读源码）**：
- 版本入口：库洛各服 `index.json`（国服 appId=10003 / B服=10004 / 国际服=50004），内含 `version`、`cdnList`（选 K1==1&&K2==1 中 P 最大的节点）、`config.indexFile`（清单地址+MD5）、`config.patchConfig[]`（"任意旧版本→当前版本"的增量入口，按旧版本号字符串精确匹配）、顶层 `predownload.config`（仅预下载窗口期出现，预更新入口）。
- 文件清单 `indexFile.json`：`resource[]`（dest 路径+md5+size）、大文件 `chunkInfos[]`、增量 `groupInfos[]`（krpdiff 差分包 + srcFiles/dstFiles 清单）。
- 下载：文件级并行（默认 8 线程）、`.temp` 临时文件 + HTTP Range 断点续传、失败重试、MD5+size 双校验。
- 增量应用：hpatchz 打补丁 → 输出 MD5 校验 → `.bak` 备份安全替换 → 按新清单事后校验并修复缺失 → 更新本地版本（`launcherDownloadConfig.json`）→ 清理。

**终末地**：2026-01-22 全球上线。国际服启动器 API `https://launcher.gryphline.com/api` 已被开源项目 LLauncher（Rust/Tauri）验证；国服走 `ak-conf.hypergryph.com`；GitHub 有 `daydreamer-json/ak-endfield-api-archive` 每 5 分钟归档全部启动器 API 响应（国服+国际服），可作联调 fixtures；v1.2.4 起采用 HDiff 增量补丁。

**依赖版本（均已核实为最新稳定非预览版）**：Avalonia 12.1.2、CommunityToolkit.Mvvm 8.4.2、Microsoft.Extensions.* 10.0.11、xunit 2.9.3、Microsoft.NET.Test.Sdk 18.9.0。

## 二、技术选型（对应需求 1、7）

| 用途 | 选型 |
|---|---|
| 运行时 | `net10.0`（本机已装 SDK） |
| UI | Avalonia 12.1.2 + 内置 FluentTheme（亮/暗双主题，`ThemeVariant` 跟随系统 + 手动切换三态） |
| MVVM | CommunityToolkit.Mvvm 8.4.2（source generator） |
| DI/配置/日志 | Microsoft.Extensions.Hosting 10.0.11、Microsoft.Extensions.Http（IHttpClientFactory） |
| JSON | System.Text.Json（内置，不用 Newtonsoft） |
| 下载 | HttpClient 流式 + Range 续传 |
| 补丁 | 官方 HDiffPatch 的 `hpatchz` 原生二进制（Linux 上原生执行，不需要 wine，优于参考仓库方案） |
| 测试 | xunit 2.9.3 + Microsoft.NET.Test.Sdk；UI 测试用 Avalonia.Headless.XUnit 12.1.2（Avalonia 官方 Headless 库） |

约束：全部使用正式稳定版；HTTP/JSON/日志/DI 只用 Microsoft 官方包；不引入 Serilog、AutoMapper 等第三方替代品。

## 三、架构与目录结构（前后端分离、模块化）

```
YetAnotherGameLauncher.slnx
├── Directory.Build.props / Directory.Packages.props   # 中央包管理、Nullable、分析器
├── src/
│   ├── YetAnotherGameLauncher.Core/            # 领域层：无 UI、无具体厂商依赖
│   │   ├── Models/        # GameCatalog/GameDefinition/GameServer、AppSettings、
│   │   │                  # GameManifest(文件清单/差分组)、LocalGameState、DownloadProgress、UpdatePlan
│   │   ├── Abstractions/  # IGameChannelApi(渠道API)、IDownloader、IManifestVerifier、
│   │   │                  # IPatchApplier、IProcessRunner、IClock（全部可注入、可测）
│   │   └── Services/      # GameCatalogService(加载/校验/保存配置)、GameInstallService(全量同步)、
│   │                      # IncrementalUpdateService(增量/预下载/应用)、GameLauncherService(启动游戏)、
│   │                      # LocalStateService、VersionPlanService
│   ├── YetAnotherGameLauncher.Channels.Kuro/    # 鸣潮渠道：KuroChannelApi + HpatchzApplier
│   ├── YetAnotherGameLauncher.Channels.Hypergryph/ # 终末地渠道：GryphlineChannelApi
│   └── YetAnotherGameLauncher.App/              # Avalonia UI（现有模板项目改造改名）
│       ├── Themes/        # Light/Dark 资源字典 + ThemeService
│       ├── Views/         # MainWindow、GameLibraryView、GameDetailView、SettingsView
│       └── ViewModels/    # MainViewModel、GameListViewModel、GameDetailViewModel、SettingsViewModel
├── tests/
│   ├── Yagl.Core.Tests / .Channels.Kuro.Tests / .Channels.Hypergryph.Tests   # xunit + 假 HttpMessageHandler + 录制 JSON fixtures
│   └── Yagl.App.Tests                                            # Avalonia.Headless.XUnit
├── samples/games.json     # 鸣潮(多服务器)+终末地示例配置
└── docs/                  # ARCHITECTURE.md / DEVELOPMENT.md / GAME_CONFIG.md（README.md 在根目录）
```

依赖方向：App → Channels → Core，单向；Core 不引用任何渠道与 UI。渠道通过 DI 按配置中的 `channel` 键注册解析，新游戏类型 = 新增一个 Channels.* 项目，不改 Core。

## 四、游戏配置设计（需求 2：零硬编码、可无缝扩展）

`~/.config/yagl/games.json`（Windows 为 `%APPDATA%\yagl`），首次运行无配置时自动落盘示例：

```jsonc
{
  "settings": { "installRoot": "~/Games", "theme": "System", "maxParallelDownloads": 8 },
  "games": [
    {
      "id": "wuthering-waves", "displayName": "鸣潮", "icon": "assets/wuwa.png",
      "channel": "kuro",
      "servers": [
        { "id": "cn", "name": "国服", "options": { "indexUrl": "https://prod-cn-alicdn-gamestarter.kurogame.com/launcher/game/G152/10003_.../index.json" } },
        { "id": "global", "name": "国际服", "options": { "indexUrl": "https://prod-alicdn-gamestarter.kurogame.com/launcher/game/G153/50004_.../index.json" } }
      ],
      "installDir": "WutheringWaves",
      "executable": "Client/Binaries/Win64/Client-Win64-Shipping.exe",
      "launch": {
        "commandTemplate": "{exe}",
        "environment": { }, "workingDirectory": "{installDir}"
      }
    },
    { "id": "arknights-endfield", "displayName": "明日方舟：终末地", "channel": "hypergryph",
      "servers": [ { "id": "global", "options": { "apiBase": "https://launcher.gryphline.com/api" } } ],
      "executable": "ArknightsEndfield/Binaries/Win64/ArknightsEndfield.exe" }
  ]
}
```

"添加新游戏"即编辑此文件（`GAME_CONFIG.md` 给出完整 schema 和教程）；同类游戏复用已有渠道 + 新端点，全新平台则新增 Channels 项目。

## 五、核心流程

1. **启动游戏**：读配置 → 解析模板命令与环境变量 → `Process.Start`（IProcessRunner 注入以便测试）→ 可选跟踪退出码。
2. **全量下载/修复**：取 index.json（校验清单 MD5）→ 下载清单 → 与本地逐文件比对（size/MD5）→ 并行下载缺失/损坏文件（`.temp`+断点续传+重试）→ 事后校验 → 写本地版本 → 清理游离文件（跳过 `Saved/`）。进度上报 ViewModel（速度/ETA/暂停/取消）。
3. **更新**：本地版本在 `patchConfig` 有匹配 → 增量路径；否则全量同步路径。
4. **预更新（两段式）**：`predownload.config` 存在时提供"预下载"→ 差分包+目标清单下载到 `{installDir}/.yagl/staging`（MD5 校验）；用户确认后"应用"→ hpatchz 合成新文件（需约 2 倍临时空间，UI 提示）→ `.bak` 安全替换与回滚 → 事后校验修复 → 更新本地版本。

## 六、TDD 开发序列（每步：先写测试 → 实现 → `dotnet test` 全绿 → 下一步）

0. 保存本计划为 `PLAN.md`；解决方案重构：多项目结构、中央包管理、.editorconfig、git init。
1. Core.Models + JSON 序列化/配置校验（含畸形配置用例）。
2. IDownloader 实现：断点续传、重试、MD5/size 校验（mock HttpMessageHandler + 临时目录）。
3. IManifestVerifier（比对/三态结果）+ 版本计划逻辑（全量 vs 增量判定）。
4. IncrementalUpdateService：差分下载 → 应用 → 回滚 → 事后修复（假渠道 API + 假补丁器，覆盖中断恢复用例）。
5. Channels.Kuro：index.json/indexFile.json 解析（真实录制 fixtures）、patchConfig 匹配、CDN 选择、下载 URL 拼接。
6. HpatchzApplier：进程调度/MD5 验证/备份回滚（IProcessRunner 假实现；真实 hpatchz 作为可选长测）。
7. Channels.Hypergryph：gryphline API 客户端（archive fixtures 驱动）。
8. GameLauncherService（命令模板解析、环境变量注入）。
9. App.Themes + ThemeService（Headless 测试：System/Light/Dark 三态切换断言 `ActualThemeVariant`）。
10. 各 ViewModel（假服务注入，测试状态机与命令可用性）。
11. Views + Headless 集成测试（绑定、按钮→命令、页面导航）。
12. Headless 端到端冒烟：加载 samples/games.json 显示两个游戏、主题跟随设置。
13. 文档与发布：`dotnet publish` linux-x64/win-x64 self-contained 验证。

## 七、UI 设计（需求 4）

FluentTheme 单窗口布局：左侧边栏为游戏列表（图标+名称，底部设置按钮），主区域为选中游戏详情页（横幅图、版本状态徽标、启动/下载/更新/预下载/校验修复按钮、下载进度条+速度+暂停取消），顶栏主题三态切换（跟随系统/亮/暗）。配色走集中资源字典，亮暗各一套 brush 覆盖；卡片式布局、圆角、留白。中文界面，文案集中在资源字典便于后续本地化。

## 八、交付物（需求 6）

- 全部源码 + 测试（含 UI Headless 测试），git 仓库（init + 分模块提交）。
- `README.md`：功能简介、Windows/Linux 构建运行说明、self-contained 发布、配置指南、Linux 下 wine/Proton 启动示例、故障排查。
- `docs/DEVELOPMENT.md`：环境要求、目录结构、各模块职责、TDD 流程与测试运行方式、如何新增渠道/新游戏。
- `docs/ARCHITECTURE.md`：模块依赖图 + mermaid 流程图（启动流程、全量下载、增量更新、预下载两段式、主题切换）。
- `docs/GAME_CONFIG.md`：games.json 完整 schema、字段说明、"添加新游戏"教程。
- `samples/games.json`：鸣潮（国服/国际服）+ 终末地示例。

## 九、风险与说明

- 终末地官方 API 无公开文档：以国际服（已被 LLauncher 验证 + archive 数据）为准实现并测试；国服端点标记为实验性，留配置位。
- 两款游戏均为 Windows 程序，Linux 运行依赖用户自备 wine/Proton——启动器通过 `commandTemplate` 配置支持，不硬编码也不代管 wine。
- 预下载仅在官方开放窗口期可用，UI 明确提示"暂未开放"。
- Avalonia.Headless.XUnit 12.x 所依赖的 xunit 主版本以实际包依赖为准（2.x 或 v3 均为 Avalonia 官方支持组合），搭建时核实后统一。