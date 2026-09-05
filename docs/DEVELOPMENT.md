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

## 2. 解决方案结构

```
src/
  YetAnotherGameLauncher.Core/                  # 领域层（无 UI/厂商依赖）
    Models/         GameCatalog/GameDefinition/GameServer、GameManifest、UpdatePlan、UpdateProgress、LocalGameState、ChannelVersionInfo
    Abstractions/   IGameChannelApi、IDownloader、IPatchApplier、IProcessRunner、异常类型
    Services/       GameCatalogService（含首次运行 CreateDefaultFileAsync）、HttpFileDownloader、ManifestVerifier、UpdatePlanner、
                    GameInstallService、IncrementalUpdateService、PackageInstallerService、
                    GameUpdateService、GameLauncherService、LocalStateService、SystemProcessRunner
    Utilities/      Hashing（MD5 hex）、Json（统一序列化选项）、InstallPath
  YetAnotherGameLauncher.Channels.Kuro/         # 库洛渠道（鸣潮）
    KuroChannelApi（index.json/indexFile 解析、CDN 选择、URL 拼接）
    KuroCdnSelector / KuroUrlBuilder、HpatchzApplier（HDiffPatch 目录模式）
  YetAnotherGameLauncher.Channels.Hypergryph/   # GRYPHLINE 渠道（终末地，包式）
    GryphlineChannelApi（batch_proxy get_latest_game）
  YetAnotherGameLauncher/                       # Avalonia UI（MVVM）
    Program.cs / App.axaml(.cs)（DI 组合根）、Themes/ThemeService、
    ViewModels/（MainWindowViewModel、GameItemViewModel、SettingsViewModel）、Views/MainWindow
tests/
  YetAnotherGameLauncher.TestSupport/           # 共享测试设施（可复用的替身与工具）
    FakeDownloader / FakePatchApplier / FakeProcessRunner / FakeChannel / StubHttpHandler / TempDir / TestZip
  YetAnotherGameLauncher.Core.Tests/            # 领域层 119 个测试
  YetAnotherGameLauncher.Channels.Kuro.Tests/   # 13 个测试
  YetAnotherGameLauncher.Channels.Hypergryph.Tests/ # 8 个测试
  YetAnotherGameLauncher.App.Tests/             # VM + Headless 窗口 14 个测试
```

构建约定（`Directory.Build.props`）：`net10.0`、`Nullable=enable`、`ImplicitUsings`、
`TreatWarningsAsErrors=true`。包版本集中管理在 `Directory.Packages.props`（CPM）。

## 3. TDD 工作流（本项目铁律）

每个功能模块遵循 **红 → 绿 → 重构**：

1. **先写测试**：在对应 `*.Tests` 项目新建测试类，定义目标行为（含边界与失败路径）。
2. **运行确认失败**：`dotnet test --project tests/...`（新用例应失败）。
3. **实现最小代码**让测试通过。
4. **全绿后提交**：`git commit`，再进入下一个模块。

本项目实际开发顺序（每步全绿后才进入下一步，见 [PLAN.md](../PLAN.md)）：

```
配置模型/校验 → 下载器 → 清单校验/版本计划 → 安装同步 → 增量应用 → 更新编排
→ 鸣潮渠道 → hpatchz 补丁器 → 终末地渠道 → 启动服务 → 主题 → ViewModels → 窗口 Headless
```

## 4. 测试布局要点

- **共享替身**（TestSupport 项目）：`FakeDownloader`（URL→字节）、`StubHttpHandler`（可模拟 Range/瞬态故障/忽略 Range）、`FakePatchApplier`（预设输出/可失败/可损坏）、`FakeChannel`（可配置版本信息与清单）、`FakeProcessRunner`、`TempDir`、`TestZip`。
- **渠道测试用真实 fixture**：鸣潮 `index.json`/`indexFile.json`、终末地 batch_proxy 响应均按真实抓包结构构造；MD5 校验链路用 `HttpFileDownloader + StubHttpHandler` 做端到端集成测试。
- **UI 测试**：`TestAppBuilder` 以 `AvaloniaTestApplication` 声明 Headless App；
  `HeadlessSession.Dispatch(...)` 在 UI 线程执行窗口级断言；ViewModel 测试纯离线。
- **注意事项**：
  - xunit.v3 要求测试项目 `<OutputType>Exe</OutputType>`；
  - App.Tests 的 RootNamespace/命名空间避免以 `App` 结尾（与 UI 程序集 `App` 类全名冲突，CS0435）；
  - 测试间状态隔离：示例配置的 `installRoot` 必须落在 `TempDir` 内；
  - `Directory.Build.props` 抑制了 `xUnit1051`（测试内文件操作无需响应取消）。

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

- 应用图标由 `tools/IconGen` 生成（headless Avalonia 渲染渐变圆角方块 + 播放三角，
  导出 16–256px PNG 并打包 ICO）：
  ```bash
  dotnet run --project tools/IconGen    # 产物写入 src/YetAnotherGameLauncher/Assets/
  ```
  窗口图标在 `MainWindow.axaml`（`Icon="avares://..."`），exe 图标在 csproj 的
  `<ApplicationIcon>`，关于页展示 `app-icon.png`。
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

两个 RID 均已验证（产物约 106MB / 209MB）。如需体积优化可追加
`-p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true`（Avalonia 兼容）。

## 8. 已知限制 / 风险

- 终末地（GRYPHLINE）协议无官方文档，字段来自社区逆向，官方启动器更新可能使其失效（隔离在渠道层，修复成本低）。
- 鸣潮预下载仅在官方窗口期可用；差分入口按版本串精确匹配，跳版本更新自动走全量。
- 包式渠道的"校验修复"粒度是压缩包（无逐文件清单），依赖解压覆盖语义。
- 终末地国服参数来自社区持续归档（ak-endfield-api-archive）而非官方文档，官方若调整协议以归档 fixture 为准修渠道层。
