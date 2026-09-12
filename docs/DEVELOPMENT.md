# 开发说明

面向参与本项目的开发者：环境搭建、目录职责、TDD 工作流、测试布局、扩展指南与发布流程。

## 1. 环境

| 组件 | 要求 |
|---|---|
| .NET SDK | **10.0**（LTS；本项目在 10.0.400 上开发） |
| IDE | Rider / VS 2026 / VS Code 均可（`YetAnotherGameLauncher.slnx`） |
| git | 必需；提交信息使用英文 conventional 风格（`feat(core): ...`） |
| 测试平台 | xunit.v3 + Microsoft.Testing.Platform（`global.json` 已声明，`dotnet test` 原生支持） |

> 注意：`global.json` 只声明了 `test.runner`，不锁定 SDK 版本。

> 文档时效性：`AGENTS.md`「文档同步」节定义了代码区域 → 文档章节的耦合地图（改动随变更同步）；全量兜底用 `/docs-sync` 技能审计，另有每周六 10:00 的定时自动化自动跑同一流程。

## 2. 解决方案结构

```
src/
  YetAnotherGameLauncher.Core/                  # 领域层（无 UI/厂商依赖）
    InstallPath.cs  安装路径解析（"~" 展开、绝对/相对 installDir；单文件位于 Core 根）
    AppPaths.cs     应用数据目录与配置文件路径（YAGL_CONFIG 覆盖；单文件位于 Core 根）
    Models/         AppSettings（全局设置）、GameCatalog/GameDefinition/GameServer、GameManifest、UpdatePlan、
                    UpdateProgress、LocalGameState、ChannelVersionInfo、DownloadRequest（单文件下载请求）、
                    LaunchOptions（启动命令模板）、ThemeMode
    Abstractions/   IGameChannelApi、IDownloader、IPatchApplier、IProcessRunner、异常类型（UpdateException/LaunchException + LaunchFailureKind）、
                    IPlatformInfo（平台环境：Linux/GPU 厂商探测、文件管理器打开目录，含 GpuVendor 枚举）、IBackdropResolver（详情页背景解析，含 BackdropKind/BackdropSource）
    Services/       GameCatalogService（含首次运行 CreateDefaultFileAsync）、HttpFileDownloader（+HttpFileDownloaderOptions）、
                    ManifestVerifier、UpdatePlanner、GameInstallService、IncrementalUpdateService、PackageInstallerService、
                    GameUpdateService、GameLauncherService（启动预检/类目化错误/启动日志）、LocalStateService、SystemProcessRunner（输出泵落盘启动日志）、
                    NetworkProxyManager（全局共享 SocketsHttpHandler，代理切换即时生效）、SpeedLimiter（泄漏桶全局限速）、
                    AutostartService.cs（IAutostartService + WindowsAutostartService（HKCU Run 注册表）/ LinuxAutostartService（XDG autostart）双实现）、
                    CompatTools（Linux 兼容层单一来源：umu/wine/Lutris/Proton 发现、prefix 统一路径、推荐链 BuildRecommendedLaunch、含 LaunchMode 枚举与 CompatLaunch）、
                    Umu/（原生 umu：UmuPaths、VdfMiniParser、SteamRuntimeCatalog、ToolManifest、UmuPrefix、UmuEnvironment、NativeUmuLauncher、IUmuComponentProvisioner）、
                    GameBackdropService（详情页背景远程解析 + 本地缓存编排）、KuroLauncherBackground（KRLauncher 官方背景探测）、
                    WebViewCacheScanner（Chromium 磁盘缓存文本流式正则提取）、WindowsPlatformInfo / LinuxPlatformInfo（IPlatformInfo 双实现）
    Utilities/      Hashing（MD5 hex）、Json（统一序列化选项）、FileUtilities（原子写入/尽力删除）
  YetAnotherGameLauncher.Channels.Kuro/         # 库洛渠道（鸣潮）
    KuroChannelApi（index.json/indexFile 解析、CDN 选择、URL 拼接）
    KuroCdnSelector / KuroUrlBuilder、HpatchzApplier（HDiffPatch 目录模式；HpatchzApplierOptions 配置路径/超时）
    KuroSwitchConfigClient（官方 switch.json 运营配置直连）、KuroBackdropResolver（背景解析：switch.json → WebView 缓存 → 本地帧序列）
    KuroGachaService（唤取记录：日志地址提取 → 官方接口 → 本地合并缓存）、KuroServiceCollectionExtensions（AddKuroChannel）、Models/（协议 DTO）
  YetAnotherGameLauncher.Channels.Hypergryph/   # GRYPHLINE 渠道（终末地，包式）
    GryphlineChannelApi（batch_proxy get_latest_game）、GryphlineProtocol（版本/背景接口共用的协议工具）
    EndfieldBackdropResolver（get_main_bg_image 背景解析：视频优先、静态图兜底）、
    HypergryphServiceCollectionExtensions（AddHypergryphChannel）、Models/（协议 DTO）
  YetAnotherGameLauncher/                       # Avalonia UI（MVVM）
    Program.cs / App.axaml(.cs)（DI 组合根）、Themes/ThemeService
    Services/       LocalizationService/ILocalizationService + LocExtension/LocBridge（JSON 资源本地化与 XAML 标记扩展）；
                    FfmpegVideoBackdropPlayer/IVideoBackdropPlayer（FFmpeg 背景视频解码播放）+ FfmpegLibraryResolver（原生库准备/下载）；
                    SeamAnalyzer（循环接缝分析）+ PrerollHandoff（预卷零间隙交接状态机）实现无缝循环；
                    BackgroundImageService（静态背景图加载与缓存，失败结果按 TTL 短暂缓存）、
                    UmuLauncherInstaller（外部 umu-run zipapp 引导安装，回退路径）、
                    UmuComponentProvisioner（原生 umu 的 Proton/Runtime 下载与校验）、
                    FilePickerService/IFilePickerService（系统文件/目录选择器封装）
    Controls/       AppBackdrop（应用背景层：主题渐变 + 光晕 + 自定义背景图）、FrameSurface（背景视频帧自绘渲染面）
    ViewModels/     MainWindowViewModel、GameItemViewModel、GameSettingsViewModel、LaunchSettingsViewModel、
                    LaunchErrorViewModel（启动失败覆盖层：类目化原因/技术详情/日志入口/umu 一键安装）、
                    GachaViewModel（鸣潮唤取记录页）、SaveMessageSlot（表单保存结果消息槽）、ViewModelBase
    Views/MainWindow
tests/
  YetAnotherGameLauncher.TestSupport/           # 共享测试设施（可复用的替身与工具）
    FakeDownloader / FakePatchApplier / FakeProcessRunner / FakeChannel / FakePlatformInfo / StubHttpHandler / TempDir / TestZip / ManualTimeProvider（虚拟时钟）
  YetAnotherGameLauncher.Core.Tests/            # 领域层 229 个测试
  YetAnotherGameLauncher.Channels.Kuro.Tests/   # 36 个测试
  YetAnotherGameLauncher.Channels.Hypergryph.Tests/ # 17 个测试
  YetAnotherGameLauncher.App.Tests/             # VM + Headless 窗口 156 个测试
  # 数量为 2026-09 实测（共 438）；随开发增长，以实际运行为准
```

构建约定（`Directory.Build.props`）：`net10.0`、`Nullable=enable`、`ImplicitUsings`、
`TreatWarningsAsErrors=true`。包版本集中管理在 `Directory.Packages.props`（CPM）。

## 3. TDD 工作流（本项目铁律）

每个功能模块遵循 **红 → 绿 → 重构**：

1. **先写测试**：在对应 `*.Tests` 项目新建测试类，定义目标行为（含边界与失败路径）。
2. **运行确认失败**：`dotnet test --project tests/...`（新用例应失败）。
3. **实现最小代码**让测试通过。
4. **全绿后提交**：`git commit`，再进入下一个模块。

本项目实际开发顺序（每步全绿后才进入下一步）：

```
配置模型/校验 → 下载器 → 清单校验/版本计划 → 安装同步 → 增量应用 → 更新编排
→ 鸣潮渠道 → hpatchz 补丁器 → 终末地渠道 → 启动服务 → 主题 → ViewModels → 窗口 Headless
```

## 4. 测试布局要点

- **共享替身**（TestSupport 项目）：`FakeDownloader`（URL→字节）、`StubHttpHandler`（可模拟 Range/瞬态故障/忽略 Range）、`FakePatchApplier`（预设输出/可失败/可损坏）、`FakeChannel`（可配置版本信息与清单）、`FakeProcessRunner`、`FakePlatformInfo`（IsLinux/NVIDIA 探测可控）、`TempDir`、`TestZip`。
- **平台相关测试**：不依赖真机 OS——`VmFactory.Build` 缺省注入 Windows 假平台（确定性），
  Linux 分支经 `platformInfo:` / `linuxProtonVersions:` 参数注入；期望值按平台分支时照
  `InstallPathTests`/`SystemProcessRunnerTests` 的 `OperatingSystem.IsWindows() ? … : …` 惯例。
- **渠道测试用真实 fixture**：鸣潮 `index.json`/`indexFile.json`、终末地 batch_proxy 响应均按真实抓包结构构造；MD5 校验链路用 `HttpFileDownloader + StubHttpHandler` 做端到端集成测试。
- **UI 测试**：`TestAppBuilder` 以 `AvaloniaTestApplication` 声明 Headless App；
  `HeadlessSession.Dispatch(...)` 在 UI 线程执行窗口级断言；ViewModel 测试纯离线。
- **注意事项**：
  - xunit.v3 要求测试项目 `<OutputType>Exe</OutputType>`；
  - App.Tests 的 RootNamespace/命名空间避免以 `App` 结尾（与 UI 程序集 `App` 类全名冲突，CS0435）；
  - 测试间状态隔离：示例配置的 `installRoot` 必须落在 `TempDir` 内；
  - `xUnit1051` 由四个测试 csproj 各自以 `<NoWarn>$(NoWarn);xUnit1051</NoWarn>` 抑制
    （测试内文件操作无需响应取消），并非在 `Directory.Build.props` 全局抑制。

## 5. 扩展指南

### 5.1 新增一个渠道（厂商协议）

1. 新建 `src/YetAnotherGameLauncher.Channels.<Name>/`，引用 Core。
2. 实现 `IGameChannelApi`：
   - `GetVersionInfoAsync`：版本 + 差分入口（文件式）或预下载状态（包式）；
   - `GetManifestAsync`：返回 `GameManifest`（**条目必须带完整下载 URL**；包式渠道置 `EntriesAreArchives=true`）；
   - `GetIncrementalManifestAsync`：文件式按需实现；包式返回 null；
   - `GetPredownloadManifestAsync`：包式预下载清单。
3. 提供 `AddXxxChannel(this IServiceCollection)`，以渠道键注册 keyed service。
4. 在 `App.BuildServices()` 注册渠道；游戏侧只需在 games.json 把 `channel` 指到新键。
5. **先写测试**：协议 DTO 用真实抓包 fixture，覆盖成功/字段缺失/MD5 不匹配/未知版本。

### 5.2 添加一个新游戏（不改代码）

同类渠道下只需编辑 `games.json`：复制现有游戏条目，改 `id/displayName/installDir/executable` 与各 `servers[].options`（端点），保存重启即可。详见 [GAME_CONFIG.md](GAME_CONFIG.md)。

### 5.3 界面本地化（i18n）

- 文案资源在 `src/YetAnotherGameLauncher/Resources/strings_zh-CN.json`（默认语言，
  缺键回退源）与 `strings_en-US.json`；**键名用下划线分节**（`settings_title`），
  不要用点号——文件名带 culture 段会被 MSBuild 拆进卫星程序集，键里的点会破坏绑定路径。
- `LocalizationService`（`Services/`）负责加载与切换，`SetLanguage` 必须同时发
  `"Item[]"` 与 `"Item"` 通知——Avalonia 的索引器绑定只认 `"Item"`（WPF 习惯的
  `"Item[]"` 不刷新）。
- XAML 中取文案用 `{svc:Loc settings_title}` 标记扩展（`LocBridge.Instance` 静态桥
  在 `MainWindowViewModel` 构造时指向当前服务）。不要写 `{Binding Loc[key]}`：
  Avalonia 对 `属性.索引器` 组合路径求值失败（静默返回空）。
- VM 内文案用注入的 `ILocalizationService`：`_loc["key"]` / `_loc.Format("key", args)`。
  `Format` 无参数时原样返回（资源串里的 `{exe}` 等占位符不会被 string.Format 误解析）。
- 新增文案：两个 JSON 同步加键（有键集一致性测试防漏译），VM/axaml 用下划线键名。
- 注意：`ReflectionBinding Loc[...]` 的组合路径与 `{Binding Loc['k']}` 引号语法均不可用。

### 5.4 应用图标与视觉自检

- 应用图标由 `tools/IconGen` 生成（headless Avalonia 绘制原创二次元少女形象——
  蓝发双马尾 + 呆毛 + 星光点缀，深蓝渐变圆角底；512 设计空间导出 16–256px PNG 并打包 ICO）：
  ```bash
  dotnet run --project tools/IconGen    # 产物写入 src/YetAnotherGameLauncher/Assets/
  ```
  窗口图标在 `MainWindow.axaml`（`Icon="avares://..."`），exe 图标在 csproj 的
  `<ApplicationIcon>`，关于页展示 `app-icon.png`，预览图输出到 `artifacts/ui-review/app-icon-preview.png`。
- UI 视觉自检循环：`UiScreenshotTests.Export_UiScreenshots_ForReview` 把真实窗口
  渲染成 PNG 输出到 `artifacts/ui-review/`（gitignore），逐张检查后再交付；
  换页后的模板构建发生在下一轮布局，截图断言前需 `window.UpdateLayout()`。

## 6. 编码规范

- 文件作用域命名空间、4 空格缩进（`.editorconfig` 强约束，TreatWarningsAsErrors）。
- JSON：统一走 `Core.Utilities.Json.Default`（camelCase、宽松读取、注释/尾逗号容忍、枚举字符串）。
- 异常：业务失败抛 `UpdateException`（消息中文化，直接可展示）；校验失败抛 `DownloadVerificationException`。
- 公共 API 有 `///` 中文注释；注释只解释"为什么"，不复述代码。
- 不引入计划外第三方包；优先 Microsoft.Extensions.* / System.*（见 `Directory.Packages.props`）。

## 7. 发布

```bash
dotnet publish src/YetAnotherGameLauncher -c Release -r linux-x64 --self-contained -o publish/linux-x64
dotnet publish src/YetAnotherGameLauncher -c Release -r win-x64   --self-contained -o publish/win-x64
```

两个 RID 均已验证（产物约 108MB / 212MB，2026-09 `du -sh` 实测）。如需体积优化可追加
`-p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true`（Avalonia 兼容）。

## 8. 已知限制 / 风险

- 终末地（GRYPHLINE）协议无官方文档，字段来自社区逆向，官方启动器更新可能使其失效（隔离在渠道层，修复成本低）。
- 鸣潮预下载仅在官方窗口期可用；差分入口按版本串精确匹配，跳版本更新自动走全量。
- 包式渠道的"校验修复"粒度是压缩包（无逐文件清单），依赖解压覆盖语义。
- 终末地国服参数来自社区持续归档（ak-endfield-api-archive）而非官方文档，官方若调整协议以归档 fixture 为准修渠道层。

## 9. 测试覆盖率政策

- 四个测试工程均接入 `coverlet.collector` + `Microsoft.Testing.Extensions.CodeCoverage`；
  采集：`dotnet-coverage collect -f cobertura -o out.xml <测试exe>`（或 dotnet test --collect）。
- **政策内 100% 目标**：Core / Channels / ViewModels / Services 的全部业务逻辑。
- **政策排除**（不计入目标，均有结构性理由）：`Program.cs` 与 `App.axaml.cs`（组合根）、
  `FilePickerService`（系统对话框封装）、`FfmpegVideoBackdropPlayer` 与 `FfmpegLibraryResolver`
  （原生库 unsafe 互操作，已由 `[ExcludeFromCodeCoverage]` 标注）、平台条件分支
  （如 WindowsAutostartService（HKCU Run 注册表）与 LinuxAutostartService（XDG autostart）
  的平台专属路径仅在对应系统运行时可达）。
- 现状与缺口清单见 `artifacts/coverage/`（本地生成，不入库）；每轮功能改动应顺带补齐所触达文件的缺口。
