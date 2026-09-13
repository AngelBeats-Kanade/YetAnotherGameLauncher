# 架构文档

本文描述 YetAnotherGameLauncher 的分层架构、模块职责与全部核心流程。
配套文档：[DEVELOPMENT.md](DEVELOPMENT.md)（开发流程）、[GAME_CONFIG.md](GAME_CONFIG.md)（配置参考）。

## 1. 分层总览

```
┌─────────────────────────────────────────────────────┐
│                 YetAnotherGameLauncher (App)         │
│   Avalonia 12 UI · MVVM · 主题 · DI 组合根           │
│   Views ←→ ViewModels (CommunityToolkit.Mvvm)        │
└───────────────┬─────────────────────────────────────┘
                │ 依赖抽象（IGameChannelApi / IDownloader / …）
┌───────────────┴─────────────────────────────────────┐
│        Channels.Kuro        │  Channels.Hypergryph   │
│  库洛协议（鸣潮）            │  GRYPHLINE 协议（终末地）│
│  index.json/indexFile 解析  │  batch_proxy 整包协议   │
│  CDN 选择 · URL 拼接        │  版本/包清单 → 包式清单  │
│  HpatchzApplier（HDiffPatch）│                        │
└───────────────┬─────────────────────────────────────┘
                │
┌───────────────┴─────────────────────────────────────┐
│                 YetAnotherGameLauncher.Core          │
│  Models（配置/清单/进度/状态）                        │
│  Abstractions（IGameChannelApi/IDownloader/…）        │
│  Services（目录/下载/校验/同步/增量/包式/编排/启动）   │
│  Utilities（Hashing/Json/InstallPath）                │
│  零 UI 依赖、零厂商依赖                               │
└─────────────────────────────────────────────────────┘
```

依赖方向严格单向：**App → Channels → Core**。渠道以 DI keyed service 注册
（键即 `games.json` 中 `game.channel`），新增渠道不需要改动 Core 与已有渠道。
操作系统差异同理：`IPlatformInfo`、`IAutostartService` 等平台抽象由组合根
按当前操作系统注入 Windows/Linux 实现，业务代码只依赖接口。

### 模块依赖图

```mermaid
flowchart TD
    APP["App (Avalonia UI)\nViewModels / Views / ThemeService"]
    KURO["Channels.Kuro\nKuroChannelApi · HpatchzApplier"]
    HY["Channels.Hypergryph\nGryphlineChannelApi"]
    CORE["Core\nModels · Abstractions · Services"]
    DI["Microsoft.Extensions.*\nDI · Logging · Http"]

    APP -->|"keyed resolve"| KURO
    APP -->|"keyed resolve"| HY
    APP --> CORE
    APP --> DI
    KURO --> CORE
    HY --> CORE
    KURO --> DI
    HY --> DI
    CORE --> DI
```

## 2. 关键抽象

| 抽象 | 职责 | 生产实现 | 测试替身 |
|---|---|---|---|
| `IGameChannelApi` | 版本查询 / 全量清单 / 增量清单 / 预下载清单 | `KuroChannelApi`、`GryphlineChannelApi` | `FakeChannel` |
| `IDownloader` | 单文件下载：Range 续传、重试、MD5/size 校验 | `HttpFileDownloader` | `FakeDownloader`、`StubHttpHandler` |
| `IPatchApplier` | 差分合成（目录模式） | `HpatchzApplier`（HDiffPatch） | `FakePatchApplier` |
| `IProcessRunner` | 外部进程（超时/输出捕获） | `SystemProcessRunner` | `FakeProcessRunner` |
| `IPlatformInfo` | OS 判定、GPU 厂商探测（NVIDIA 走 /proc 闭源驱动，AMD/Intel 走 /sys/class/drm 的 PCI vendor；路径可注入）、用文件管理器打开目录 | `WindowsPlatformInfo`、`LinuxPlatformInfo`（DI 按 OS 注入） | `FakePlatformInfo` |
| `IAutostartService` | 开机自启查询/切换（Windows 注册表 / Linux XDG autostart） | `WindowsAutostartService`、`LinuxAutostartService`（DI 按 OS 注册） | 真实现 + FakeProcessRunner（VmFactory 缺省）；集成测试按平台注入真实现 |
| `IBackdropResolver` | 按区域解析详情页背景来源（图/视频 + 首帧海报） | `KuroBackdropResolver`、`EndfieldBackdropResolver`（按渠道键 keyed 注册） | 测试内联假实现 |
| `GameCatalogService` | games.json 加载/校验/原子保存 | — | 配置 fixture |
| `GameInstallService` | 全量同步（校验→并行下载→事后校验→清理） | — | 假下载器 |
| `IncrementalUpdateService` | 差分两段式（暂存→应用/回滚） | — | 假补丁器 |
| `PackageInstallerService` | 包式两段式（下载→解压） | — | 假下载器 |
| `GameUpdateService` | 面向 UI 的编排入口 | — | 全假组件 |
| `GameLauncherService` | 命令模板解析与启动 | — | `FakeProcessRunner` |
| `ThemeService` | ThemeVariant 三态切换 | — | — |

两类渠道分发模型，由 `GameManifest.EntriesAreArchives` 区分：

- **文件式（鸣潮）**：清单 = 最终游戏文件（path/size/md5），差分入口 `patchConfig` 按旧版本号精确匹配。
- **包式（终末地）**：清单 = 压缩包（packs），下载解压即安装；无按版本差分，更新=请求新版本整包，预下载=响应 `patch` 节点。

## 3. 核心流程

### 3.1 启动游戏

```mermaid
sequenceDiagram
    participant U as 用户
    participant VM as GameItemViewModel
    participant LS as GameLauncherService
    participant PR as IProcessRunner

    U->>VM: 点击"启动"
    VM->>LS: LaunchAsync(game, installDir, exe)
    LS->>LS: 预检：主程序存在
    LS->>LS: 展开模板：{exe} {installDir}<br/>工作目录、环境变量
    LS->>LS: 预检：运行时存在且可执行<br/>（缺执行位自动 chmod +x）<br/>创建 WINEPREFIX/STEAM_COMPAT 目录
    LS->>PR: RunAsync(spec，WaitForExit=false，<br/>OutputLogPath=启动日志)
    PR-->>LS: 进程已启动（即启即走，立即返回）
    LS-->>VM: LaunchResult(退出码, 日志路径)
    VM->>U: "游戏已启动" / 失败弹错误卡
```

启动是**即启即走**（fire-and-forget）：`ProcessStartSpec.WaitForExit = false`，
启动器不等待游戏退出、不杀进程树。游戏 stdout/stderr 经输出泵写入
`~/.local/share/yagl/logs/launch-<游戏id>-<时间戳>.log`（逐行读取到 EOF——
不用 `BeginOutputReadLine` 事件：`WaitForExit()` 只排空 stdout，stderr 会丢；
日志路径模式的进程句柄在泵收尾后才释放，`using` 作用域提前 Dispose 会掐断管道）。
游戏秒退的排查线索由日志尾注 + 应用日志共同记录：退出码与存活时长。

**预检**（`BuildPlan` 内）：① 主程序存在（`ExecutableMissing`）；② 模板首段可执行——
裸命令名按 PATH 解析（找不到 → `RuntimeMissing`），绝对路径缺执行位自动补
`chmod +x`（从压缩包解出的 proton 脚本常见，补不上 → `RuntimeNotExecutable`）；
③ `WINEPREFIX` / `STEAM_COMPAT_DATA_PATH` 目录创建（Proton 要求已存在，
失败 → `PrefixCreateFailed`）。失败抛 `LaunchException`（`UpdateException` 子类，
携带 `LaunchFailureKind` 与日志路径），UI 据此弹主题化错误覆盖层：
类目化中文原因 + 可折叠技术详情 + 打开日志目录；umu 模板且 umu 未装时提供一键安装
（`UmuLauncherInstaller` 从 GitHub release 拉 zipapp 解到应用数据目录）。

Windows 上若游戏可执行文件的清单要求管理员权限（requireAdministrator），
`CreateProcess` 抛 Win32Exception 740（ERROR_ELEVATION_REQUIRED，无法自提升）：
`SystemProcessRunner` 换用 `UseShellExecute = true` 的全新 `ProcessStartInfo`
经 ShellExecute 启动，由系统弹 UAC；该路径不支持按进程注入环境变量，
配置了自定义环境时记警告放弃。

命令模板支持引号包裹（含空格路径），例如 `wine "{exe}"`；
Linux 上如何运行（umu / 直接运行）完全由配置决定，代码零平台假设。
设置页启动方式二选一：**umu 启动**（默认，`native-umu {exe}`）与**直接运行**（`{exe}`，Windows 唯一方式）；
旧版 wine/Proton/外部 umu-run 模板仍可执行，仅不再出现在选择器中。
umu 模式旁有 **Proton 发行版选择**（DW-Proton / GE-Proton / UMU-Proton，默认 DW-Proton）：
代号写入 `environment.PROTONPATH`，启动解析与组件准备共用它（`CompatTools.ResolveNativeProtonRequest`
优先读 PROTONPATH），代号语义 = 按对应仓库拉 latest、离线回退该前缀本地最新。
UMU_ID 由 `launch.umuId` 覆盖（对齐 umu 数据库规范 ID：鸣潮 `umu-3513350`、终末地 `umu-endfield`）。
推荐链单一事实源在 `CompatTools.BuildRecommendedLaunch`（默认原生 umu）。原生链在
`NativeUmuLauncher` + `IUmuComponentProvisioner`：

1. 解析/下载 Proton（DW-Proton [dawn.wine Forgejo] / GE-Proton / UMU-Proton [GitHub]
   → `~/.local/share/Steam/compatibilitytools.d`；DW 资产 tar.xz 且目录名去架构后缀）
2. 读 `toolmanifest.vdf` 得所需 Steam Runtime，缺失则下载到 `~/.local/share/umu/<variant>`（SHA256 校验）
3. `UmuPrefix.Setup` 布好 Proton 兼容 prefix（pfx 符号链接、shadercache、steamuser）
4. `UmuEnvironment.Build` 写完整 STEAM_COMPAT_* / UMU_* 环境
   （与上游 umu_run.py 对齐：`STEAM_COMPAT_APP_ID` 恒为 prefix 路径 MD5、`STORE` 缺省空串；
   配置里的 PROTONPATH 代号不覆盖已解析的绝对路径）
5. 经 `{runtime}/_v2-entry-point --verb=… -- {proton}/proton <verb> {exe}` 启动（`IProcessRunner`，即启即走）

外部 `umu-run` zipapp 路径保留为回退（`UmuLauncherInstaller`，仅供存量模板与错误覆盖层）。
Wine prefix 统一在 `{数据目录}/yagl/prefixes/<游戏id>`（`STEAM_COMPAT_DATA_PATH` 同址），
绝不写入游戏安装目录——安装同步的清单外清理不会误删 prefix（`compatdata` 另在保留名单纵深防御）。
（唯一例外是首运配置生成：Linux 会把默认 `{exe}` 升级为推荐链再落盘，见 GAME_CONFIG.md。）

### 3.2 全量同步（文件式）

```mermaid
flowchart TD
    A[取渠道 index.json<br/>校验清单 MD5] --> B[VerifyFast：存在性+大小比对]
    B --> C{有缺失/损坏?}
    C -- 否 --> F
    C -- 是 --> D[并行下载<br/>.temp + Range 续传 + 重试]
    D --> E[VerifyFull：逐文件 MD5]
    E -- 失败 --> G[抛出 UpdateException<br/>列出问题文件]
    E -- 通过 --> F[清理游离文件<br/>保留 Saved/ 与 .yagl/]
    F --> H[(写入 .yagl/state.json 版本)]
```

### 3.3 增量更新（鸣潮 krpdiff，两段式）

```mermaid
flowchart TD
    A[index.json] --> B{本地版本 ∈ patchConfig?}
    B -- 否 --> FULL[全量同步路径]
    B -- 是 --> C[下载差分清单<br/>校验 indexFileMd5]
    C --> D[预下载：krpdiff 差分包 + 新文件<br/>暂存 .yagl/predownload]
    D --> E[应用：逐组执行]
    E --> E1[复制 SrcFiles → olddir]
    E1 --> E2[hpatchz -f olddir patch newdir]
    E2 --> E3{输出 MD5 = DstFiles?}
    E3 -- 否 --> E4[抛错，游戏本体未动]
    E3 -- 是 --> E5[.yagl-bak 备份替换]
    E5 -- 任一失败 --> E6[整组回滚]
    E5 -- 成功 --> E7[按目标清单事后校验<br/>缺失文件自动修复]
    E7 --> H[(更新 .yagl/state.json)]
```

要点（与参考实现 wutheringwaves-cli-manager 对齐）：

- 差分入口按**字符串精确匹配**本地版本；
- 单组失败只回滚该组（已成功的组保留），整体可重试直至成功；
- 中断后可重复"应用"：目标文件已就绪的组自动跳过；
- 事后校验使用目标版本全量清单，缺失文件走全量修复路径。

### 3.4 包式安装/预下载（终末地）

```mermaid
flowchart TD
    A[POST batch_proxy<br/>get_latest_game] --> B[pkg.packs → 包清单<br/>EntriesAreArchives]
    B --> C[并行下载压缩包<br/>size+MD5 校验]
    C --> D[Zip 解压进安装目录]
    D --> E[清理临时包]
    B -.预下载窗口.-> F[patch 节点 → 预下载包清单]
    F --> G[暂存 .yagl/predownload/packages]
    G --> H[用户确认 → 解压落盘 + 更新版本]
```

### 3.5 更新编排（GameUpdateService）

```mermaid
flowchart TD
    A[读取 .yagl/state.json] --> B[channel.GetVersionInfoAsync]
    B --> C[UpdatePlanner.Plan]
    C -->|未安装 或 无差分| D[全量：文件式 Sync / 包式 Install]
    C -->|差分可用| E[增量：Predownload+Apply hpatchz]
    D --> F[事后校验修复]
    E --> F
    F --> G[(保存 state.json 版本)]
```

### 3.6 主题切换

```mermaid
flowchart LR
    U[用户选择] --> T[ThemeService.Apply]
    T -->|"System"| D1["RequestedThemeVariant = Default<br/>（跟随 OS）"]
    T -->|"Light"| D2["= Light"]
    T -->|"Dark"| D3["= Dark"]
    D1 & D2 & D3 --> R["ResourceDictionary.ThemeDictionaries<br/>按 ActualThemeVariant 取刷子"]
    R --> UI[界面即时换肤]
```

`Apply` 可从任意线程调用（后台初始化场景）：跨线程时经 `Dispatcher.Post` 投递。

### 3.7 背景视频播放子系统

详情页背景的解析、缓存与视频播放管线：配置文件不携带背景地址，每次按区域向渠道确认当期背景，
缓存到本地后由 FFmpeg 播放器后台解码上屏，并以循环点分析 + 预卷做到无缝循环。关键机制：

- **背景解析**：keyed `IBackdropResolver` 按渠道解析——
  `kuro`：官方运营配置 switch.json 的 `BackgroundFile`/`FirstFrameImage`（背景视频 + 首帧图），
  回退本机库洛启动器缓存与 `kr_game_cache` 帧探测（缓存根按平台探测：Windows 取
  `%APPDATA%\KRLauncher`；Linux 逐 Wine/Proton prefix 探测官启数据目录，`KuroLauncherBackground.LinuxCacheRoots`）；
  `hypergryph`：官方启动器 `get_main_bg_image` 接口（视频优先、静态图兜底）。
  `GameBackdropService` 把远程背景流式下载缓存到 `%ConfigDirectory%/backdrops/<gameId>/`
  （`backdrop.*` + `poster.*` + `meta.json`），地址未变不重复下载，离线/下载失败回退上次缓存。
- **播放**：`FfmpegVideoBackdropPlayer` 后台线程解码（Windows D3D11VA / Linux VAAPI→CUDA(NVDEC)
  硬解，按序尝试、设备创建失败自动落到下一项直至回软解；硬解 GPU 帧经 `av_hwframe_transfer_data`
  回读系统内存——回读不拷贝帧属性，pts 必须在回读前从原始解码帧捕获），swscale 转 BGRA 后逐行 blit 进
  `WriteableBitmap`，16ms 节流通知 UI 重绘；`PlaybackClock` 按 PTS 实时节拍
  （落后超阈值重定基线，不做爆发追帧），渲染尺寸 clamp ≤1080p，静音不解码音轨。
  硬解日志语义：设备创建成功/失败（含 `av_strerror` 错误文本）各一条，`avcodec_open2` 后再打一条
  协商出的像素格式（`vaapi_vld (hardware)` / `yuv420p (software)`）——设备创建成功 ≠ 硬解生效，
  协商格式才是判据；循环点分析源刻意纯软解，不打硬解日志（勿把"软解分析"误读成回退失败）。
- **原生库供给**（`FfmpegLibraryResolver`，与 FFmpeg.AutoGen 9.0 绑定精确配套 = libavcodec 主版本 63）：
  应用数据目录已下载库 → 系统库（Linux 探测 `libavcodec.so.63`——其它主版本 ABI 不配套会崩，宁缺毋滥；
  旧实现拼出 `libavcodec-63.dll`/裸 `dlopen("avcodec")`，Linux 上永远失败，是"背景视频没了"的根因）→
  下载 BtbN LGPL 共享构建（SHA256 校验后解压到 `%ConfigDirectory%/ffmpeg/<rid>/`）。
- **无缝循环**：`SeamAnalyzer` 把头/尾各约 2s 的帧缩为 64×36 灰度缩略，搜索帧对平均绝对差最小的
  循环点，阈值内命中则循环从该点起播（起播不 seek，从流头顺序读取、渲染前丢弃起点之前的帧）；
  临近循环终点前 2s 由第二个解码源后台预解码下一循环开头 10 帧，经 `PrerollHandoff` 交接状态机
  交接，到达终点时预卷源整体收编、先消费预解码帧，零间隙续播；收编时按接缝差自适应——
  差值在阈值内直接硬化切，否则 0.6s 交叉淡化。预卷未就绪回退为重开全新解码源 + 交叉淡化。
- **关键约束**：本机 BtbN FFmpeg n9.0 构建的 mov demuxer 上 seek 不可靠——
  所有路径一律顺序读取 + 帧丢弃对齐，禁止带时间戳的 seek。

## 4. 配置与状态的数据流

- **配置（输入）**：`games.json`（渠道键、服务器选项、启动模板）→ `GameCatalogService` 解析 + 全量语义校验（错误集中返回）。
- **状态（输出）**：`{installDir}/.yagl/state.json`（gameId/serverId/Version，防错配）；预下载暂存 `.yagl/predownload/`（含 `manifest.json`，应用阶段读取）。
- **不修改**官方启动器的兼容文件（如 `launcherDownloadConfig.json`、`Saved/` 存档目录在清理时保留）。

## 5. 线程模型

- ViewModel 命令在 UI 线程触发，网络/磁盘在任务池并行（`Parallel.ForEachAsync`，并行度为内部选项 `GameInstallServiceOptions.MaxParallelFiles`，默认 8，不作为用户配置项开放）。
- 进度经 `Progress<T>` 回传（捕获同步上下文），聚合计数用 `Interlocked`/锁。
- `HttpFileDownloader` 内部自管理 `.temp` 文件，多文件并发互不相交。

## 6. 安全设计

- 清单路径经 `ManifestVerifier.ResolveSafe` 越界检查（拒绝 `..`、绝对路径、盘符）。
- 所有从 CDN 下载的字节按 `size`+`MD5` 校验后才落位（`index.json` 清单本身也校验 `indexFileMd5`）。
- 差分输出先校验再替换；替换带备份回滚，任何失败不破坏既有安装。
- GRYPHLINE 协议字段来自社区逆向，已隔离在渠道项目内并可配置 `apiBase`。
