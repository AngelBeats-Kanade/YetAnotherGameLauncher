---
feature: native-umu-launcher
status: delivered
updated: 2026-02-14
branch: feat/native-umu-launcher
commits: c977def..d0a3883
---

# Native umu-launcher（纯 C# 替换 umu-run）

## Report

**What was built** — Linux 默认启动链改为**原生 C# umu**（模板 token `native-umu {exe}`），不再依赖 Python `umu-run`。Core 新增 `Services/Umu/`：路径、VDF/toolmanifest、Steam Runtime 映射、prefix（setup_pfx + FileStream 锁）、完整 `STEAM_COMPAT_*` 环境、`NativeUmuLauncher`（组件解析 → prefix → env → `_v2-entry-point` 启动）。App 层 `UmuComponentProvisioner` 下载/校验 GE/UMU-Proton 与 Steam Runtime。推荐链默认原生 umu；外部 umu-run 仍可选。设置卡显示组件状态并可「检查/下载」；组件下载失败的启动错误卡可「重试」。无 `unsafe`、无 P/Invoke。

**Verification** — worktree `--no-restore`（本机 NuGet restore path1 损坏，PRE-EXISTING）：
- `dotnet build` App/Core/tests `-warnaserror`：PASS
- Core.Tests：228 PASS；App.Tests：150 PASS；Kuro：36；Hypergryph：17
- `dotnet format whitespace --verify-no-changes`：PASS

**Journey log** —
1. 上游 umu 主路径可纯 C# 移植；容器仍走 Runtime 自带 `_v2-entry-point`。
2. 推荐链默认切原生后，first-run/推荐链测试改为断言 `native-umu`；旧链用例加 `preferNativeUmu: false`。
3. Review：缺失版本不得偷换任意本地 Proton；版本用自然序；entry argv 不能整串按空格 split。
4. 原生启动路径绕过 `GameLauncherService`，env 占位符展开须在 `BuildPlan` 内对齐。
5. 真机 Linux E2E（真实下载 Proton/Runtime）与「失败后手动选本机 Proton」按钮仍待后续。

## [S1] Problem

YetAnotherGameLauncher 在 Linux 上把 Windows 客户端（鸣潮 / 终末地）跑起来，目前依赖外部 Python `umu-run`：

- 安装器只是从 GitHub release 拉 zipapp 解出 `umu-run`，运行时仍要 Python 与 umu 自己的依赖链。
- 本仓库只生成了薄薄一层环境（`GAMEID` / `UMU_ID` / `WINEPREFIX`）并 shell 到 `umu-run`；prefix 结构、完整 Steam 兼容环境、Proton/Runtime 解析与下载，全部在进程外的 Python 里。
- 诊断困难：缺 Proton / 缺 Steam Runtime / prefix 结构不对时，错误来自 umu，启动器只能笼统报「运行时缺失」。
- 用户要求：**把 umu-launcher 相关能力移植成纯 C#（禁止 `unsafe`），默认不再依赖外部 umu-run**；缺资源时由启动器自动下载 Proton 与 Steam Runtime。

## [S2] Design

### 2.1 总体目标

在 Core 增加 **原生 umu 启动链**，覆盖 `umu-run` 主路径中启动器真正需要的部分，用 `IProcessRunner` 发出最终进程：

```
[可选下载] Steam Runtime + GE/UMU-Proton
        ↓
setup_pfx（Proton 兼容 prefix）
        ↓
set_env（完整 STEAM_COMPAT_* / UMU_* / PROTON_*）
        ↓
build_command：runtime/_v2-entry-point --verb=… -- proton <verb> <exe> …
        ↓
IProcessRunner（即启即走 + 启动日志，沿用现有 GameLauncherService 风格）
```

**不做**（明确 out of scope，见 [S3]）：Gamescope X11 属性、`prctl` 子收割者、winetricks 动词管理、umu TOML 配置文件、pressure-vessel nsenter 重入、zenity 进度框。

### 2.2 分层与落点

| 层 | 类型 | 职责 |
|---|---|---|
| Core | `UmuPaths` | XDG 数据/缓存路径：`~/.local/share/umu`、`~/.local/share/Steam/compatibilitytools.d`、`~/.cache/umu`（可注入 home/data/cache 根，测试友好） |
| Core | `ToolManifest` / `VdfMiniParser` | 解析 Proton 的 `toolmanifest.vdf`（仅需 `require_tool_appid`、`commandline`、`compatmanager_layer_name`）与 `compatibilitytool.vdf` 的 `display_name`；手写最小 VDF 读取器，不引第三方 |
| Core | `UmuPrefix` | 移植 `setup_pfx`：`pfx→.` 符号链接、`shadercache/`、`gstreamer-1.0/`、`tracked_files`、`drive_c/users/{steamuser,当前用户}` 双向链接；用 `FileStream` 独占创建 `pfx.lock` 做互斥（不用 flock 系统调用、不用 unsafe） |
| Core | `UmuEnvironment` | 移植 `check_env` + `set_env` + `enable_steam_game_drive` 的环境字典：`GAMEID`/`UMU_ID`/`STORE`/`PROTON_VERB`/`PROTONPATH`/`WINEPREFIX`/`STEAM_COMPAT_*`/`SteamAppId`/`SteamGameId`/`STEAM_RUNTIME_LIBRARY_PATH` 等；`STEAM_COMPAT_APP_ID` = prefix 路径 MD5 十六进制（与上游一致） |
| Core | `ProtonCatalog` | 发现本机 Proton（沿用 `CompatTools.ProtonRoots`）；解析 toolmanifest → 所需 runtime appid → 映射 soldier/sniper/steamrt4 |
| Core | `ProtonDownloader` | `PROTONPATH` 为空或为代号（`GE-Proton`/`UMU-Proton`/`GE-Latest`/`UMU-Latest`）时，经 GitHub API 选最新 release 资产（`.tar.gz`），SHA512 旁路资产存在则校验；解压到 `compatibilitytools.d`；已有本地最新则跳过 |
| Core | `SteamRuntimeInstaller` | 按 runtime 定义从 `repo.steampowered.com` 下载 `SteamLinuxRuntime_*.tar.xz` + `SHA256SUMS` + `BUILD_ID.txt`；Range 续传；解压到 `~/.local/share/umu/<variant>`；写 `.installed.ok` 标记；并发用同一锁文件 |
| Core | `NativeUmuLauncher` | 编排：解析 Proton → 解析/准备 Runtime → setup_pfx → set_env → 组装命令 → `IProcessRunner` 启动；错误映射到现有 `LaunchException` / `LaunchFailureKind`（新增 kind 如 `UmuRuntimeMissing`/`ProtonDownloadFailed` 若现有枚举不够用） |
| Core | `UmuLaunchPlan` | 最终 `FileName`/`Arguments`/`WorkingDirectory`/`Environment`，与 `LaunchPlan` 兼容或复用 |
| App | DI + 设置卡 | 注册服务；启动设置增加「原生 umu（内置）」；默认推荐链改为 **原生 umu → 外部 umu-run（可选）→ Proton 直启 → wine**；一键安装 umu zipapp 降级为可选/高级项 |
| 测试 | Core.Tests / App.Tests | 路径、prefix 结构、环境变量、VDF 解析、命令拼装、下载器假件、启动失败覆盖层 |

**禁止**：任何 `unsafe` 关键字、指针、`stackalloc` 逃逸、手动内存；不 P/Invoke `prctl`/`flock`/`renameat2`。

### 2.3 关键契约

#### 2.3.1 Prefix 布局（与 umu `setup_pfx` 对齐）

给定 `WINEPREFIX = {data}/yagl/prefixes/{gameId}`（沿用现有统一 prefix 约定，**不**用 umu 默认的 `~/Games/umu/GAMEID`）：

- `{pfx}/pfx` → 符号链接到 `.`（若已是目录则保留；若已是坏 symlink 先删）
- `{pfx}/shadercache/`、`{pfx}/gstreamer-1.0/` 目录
- `{pfx}/tracked_files` 空文件
- `{pfx}/drive_c/users/steamuser` 与当前 Unix 用户名互链（两用户目录都不存在时建 steamuser、用户 symlink → steamuser；只存在一边则补另一边 symlink）

Windows 主机上 Native umu 路径不激活（`OperatingSystem.IsLinux()` 门禁），相关测试在 Linux 语义下用注入路径跑；Windows CI 只测纯逻辑（环境字典、VDF、命令拼装）。

#### 2.3.2 环境变量（`NativeUmuLauncher` 生成后写入进程环境，同时并入配置 `environment`）

必设/推导：

| 键 | 来源 |
|---|---|
| `GAMEID` | 配置或 `umu-{gameId}`（与现有 `BuildUmuLaunch` 一致） |
| `UMU_ID` | 同 `GAMEID` |
| `STORE` | 可配置，默认 `none` |
| `WINEPREFIX` | 统一 prefix 路径（绝对） |
| `STEAM_COMPAT_DATA_PATH` | 同 `WINEPREFIX` |
| `STEAM_COMPAT_SHADER_PATH` | `{WINEPREFIX}/shadercache` |
| `PROTONPATH` | 解析后的 Proton 绝对目录 |
| `PROTON_VERB` | 默认 `waitforexitandrun`，可覆盖 |
| `STEAM_COMPAT_INSTALL_PATH` | exe 所在目录 |
| `STEAM_COMPAT_CLIENT_INSTALL_PATH` | Steam 根（`~/.steam/steam`） |
| `STEAM_COMPAT_TOOL_PATHS` / `STEAM_COMPAT_MOUNTS` | `PROTONPATH[:RUNTIMEPATH]` |
| `STEAM_COMPAT_APP_ID` | 前半段：prefix 路径 MD5 hex（与上游 umu 当前行为一致） |
| `SteamAppId` / `SteamGameId` | 上游在 umu-id 数字后缀时覆盖；本项目 gameId 非纯数字时保持 `0` 或沿用 umu-id 解析规则 |
| `EXE` | 游戏可执行文件绝对路径 |
| `STEAM_RUNTIME_LIBRARY_PATH` | 安装路径 + 系统 ldconfig 前缀（可简化：安装路径 + `LD_LIBRARY_PATH`；完整 ldconfig 解析若无 unsafe/外部进程依赖过重则记为后续增强） |
| `PROTON_CRASH_REPORT_DIR` | `{tmp}/yagl-umu-crashreports` |

游戏推荐环境仍叠加现有 `CompatTools.RecommendedEnvironment`（如鸣潮 `SteamOS=1`、NVIDIA `PROTON_ENABLE_NVAPI=1`）。

#### 2.3.3 Runtime 映射

| appid | name | variant | 机器 |
|---|---|---|---|
| 1391110 | soldier | steamrt2 | x86_64 |
| 1628350 | sniper | steamrt3 | x86_64 |
| 4183110 | steamrt4 | steamrt4 | x86_64 |
| 4185400 | steamrt4-arm64 | steamrt4-arm64 | aarch64 |

现代 GE-Proton / UMU-Proton 的 `require_tool_appid` 通常为 steamrt4。下载端点：

`https://repo.steampowered.com/{variant}/images/{version}/SteamLinuxRuntime_{name}.tar.xz`

版本选择：上游通过 VERSIONS/镜像目录探测；本实现 **v1 固定使用 umu-launcher 当前锁定的已知版本号常量**（实现时从 `umu-runtime` 源码抄最新常量并写进 `SteamRuntimeInstaller`，带注释来源），并支持环境/设置覆盖。不实现平台镜像自动探测的完整逻辑。

#### 2.3.4 命令组装

```
{UMU_LOCAL}/{variant}/_v2-entry-point --verb={PROTON_VERB} -- {PROTONPATH}/proton {PROTON_VERB} {EXE} [args…]
```

- Runtime 未安装且禁止下载时：`LaunchException(RuntimeMissing)`，文案指向设置页「检查兼容层组件」。
- Proton 目录存在但缺 `proton` 可执行位：沿用现有 chmod 补权逻辑。
- 工作目录：游戏安装目录（与现行为一致；非 winetricks）。

#### 2.3.5 推荐链与回退

`CompatTools.BuildRecommendedLaunch`（或新的 `BuildRecommendedLaunchV2`）在 Linux 上改为：

1. **原生 umu**（始终可生成计划；执行前由 `NativeUmuLauncher` 确保 Proton+Runtime 就绪或下载）
2. 外部 `umu-run`（仅当用户显式选择或原生路径被禁用）
3. Proton 直启
4. 系统 wine  
5. 仍无 → 原生 umu 模板（触发组件准备，而不是再装 Python zipapp）

`LaunchMode` 增加 `NativeUmu`（或扩展 `Umu` 的 `RuntimeName` 区分 `native` / `external`）。UI 选择器文案：「umu（内置）」「umu（外部 umu-run）」。

#### 2.3.6 错误与 UI

| 失败 | Kind（沿用/新增） | 用户可见动作 |
|---|---|---|
| Proton 下载失败 | `ProtonDownloadFailed`（新增） | 重试；打开日志；回退手动选择本机 Proton |
| Runtime 下载/校验失败 | `UmuRuntimeDownloadFailed`（新增） | 重试；说明需要网络 |
| prefix 创建失败 | 现有 `PrefixCreateFailed` | 检查磁盘/权限 |
| 启动命令失败 | 现有 `StartFailed` / `RuntimeMissing` | 检查组件完整性 |

启动设置卡显示组件状态：Proton 版本、Runtime 是否就绪、上次检查时间；提供「检查/下载兼容组件」按钮（进度走现有 Progress 模式）。

#### 2.3.7 下载与校验

- 复用 `IDownloader`（Range 续传、重试）。
- Proton：GitHub API `releases/latest`（User-Agent 必填）；资产匹配 `GE-Proton*` 或 `UMU-Proton*` 的 `.tar.gz`；可选 `.sha512sum`；解压后校验目录内存在 `proton` + `toolmanifest.vdf`。
- Runtime：`SHA256SUMS` 按文件名匹配校验后再落位；解压 `.tar.xz` 用现有压缩库能力（SharpCompress 已在 App 用于 tar；若 Core 不便引用，则下载服务放 App 层，Core 只定义接口 `IUmuComponentProvisioner`）。

**分层裁决**：网络大文件下载与解压实现放 **App 层服务**（`UmuComponentProvisioner`，实现 `IUmuComponentProvisioner`），Core 只定义接口 + 纯逻辑。与 `UmuLauncherInstaller` 现状一致（App 下载、Core 发现）。

#### 2.3.8 并发与锁

- 锁文件：`{data}/yagl/umu-locks/{runtime|proton|prefix}.lock`，`FileStream` + `FileMode.OpenOrCreate` + `FileShare.None` + 有限重试/超时。
- 同一游戏 prefix：启动前独占；组件安装全局一把锁（避免并发双下）。

### 2.4 测试边界

- **纯逻辑（任意 OS）**：VDF 解析、环境字典完整键集、命令字符串、runtime 映射、GitHub 资产选择、SHA256SUMS 解析。
- **文件系统（TempDir）**：prefix 布局（Windows 上跳过 symlink 断言或标记 Linux-only）、安装标记、锁超时。
- **进程（FakeProcessRunner）**：`NativeUmuLauncher` 发出的 FileName/Arguments/Env。
- **网络（StubHttpHandler / FakeDownloader）**：Proton/Runtime 下载与校验失败路径。
- **UI headless**：启动设置显示内置 umu；缺组件错误覆盖层按钮；**不**断言动画。

不把真实 GitHub/Steam CDN 请求写进测试。

## [S3] Out of Scope

- Gamescope / X11 window property（`STEAM_GAME` atom、baselayer）
- `PR_SET_CHILD_SUBREAPER`、信号处理细节
- winetricks 动词安装/去重、`UMU_NO_PROTON` 原生 Linux 游戏
- umu `--config` TOML
- nsenter / `UMU_CONTAINER_NSENTER` 重入容器
- Flatpak `HOST_XDG_DATA_HOME` 特殊布局（可后续增强）
- umu-database 在线查 GAMEID（继续用 `umu-{gameId}` 约定）
- 完整 `ldconfig -p` 库路径枚举（v1 用简化集合）
- 移除已存在的外部 umu-run 安装器代码（保留但 UI 降级；删除留给后续 PR）
- arm64 主机的完整支持矩阵（代码按映射表可扩展，测试与 CI 仅 x86_64）

## Tasks

- [x] T1: Core 路径与 VDF — `UmuPaths` + 最小 VDF 解析 + runtime 映射表；acceptance: 单测覆盖 toolmanifest 关键字段与 appid→variant（covers: S2.2, S2.3.3）
- [x] T2: `UmuPrefix` — 移植 setup_pfx + 文件锁；acceptance: TempDir 下布局与幂等单测（Linux symlink 断言）（covers: S2.3.1）
- [x] T3: `UmuEnvironment` — 完整环境字典与 MD5 app id；acceptance: 键集与值规则单测（covers: S2.3.2）
- [x] T4: `NativeUmuLauncher` 命令组装与启动 — 不含下载；acceptance: FakeProcessRunner 断言命令行与 env（covers: S2.3.4）
- [x] T5: `IUmuComponentProvisioner` + App 实现 — Proton 下载、Runtime 下载校验、锁；acceptance: Stub/Fake 下载单测（covers: S2.3.7, S2.3.8）
- [x] T6: 推荐链/`LaunchMode`/设置卡 — 内置 umu 为默认 Linux 推荐；组件状态与「检查/下载兼容组件」按钮（covers: S2.3.5, S2.3.6）
- [x] T7: 错误覆盖层与本地化 — 新 LaunchFailureKind + strings 成对；下载失败可重试（covers: S2.3.6）
- [x] T8: 文档同步 — ARCHITECTURE.md §3.1、GAME_CONFIG.md launch 说明、DEVELOPMENT.md 目录；acceptance: 与实现一致（covers: S2）
- [x] T9: 全量验证 — `dotnet build -warnaserror` + 测试 exe + format verify；acceptance: 零警告、测试通过（covers: S2）
