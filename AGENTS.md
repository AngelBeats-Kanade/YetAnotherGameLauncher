# AGENTS.md

鸣潮 / 明日方舟：终末地 的跨平台游戏启动器。Avalonia 12 + .NET 10 + CommunityToolkit.Mvvm，Linux + Windows。

## 必读文档

改动前先读 `docs/`：**ARCHITECTURE.md**（分层/核心流程）、**DEVELOPMENT.md**（目录职责/TDD 工作流）、**GAME_CONFIG.md**（games.json 配置参考）。UI/测试工作前加载 `.zcode/skills/` 下对应技能（avalonia-ui、avalonia-headless-testing、avalonia-ui-review、desktop-ui-design）。

## 常用命令

```bash
dotnet build -warnaserror            # 全解决方案构建；零警告是硬约束（TreatWarningsAsErrors=true）
dotnet format whitespace --verify-no-changes
# 测试（xunit.v3 + MTP；本环境 dotnet test 可能发现 0 个测试——直接跑测试可执行文件更可靠）：
./tests/YetAnotherGameLauncher.App.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.App.Tests.exe
# 单个测试：dotnet test --project <csproj> --filter-fqn "<完整类型名>.<方法名>"
```

## 文档同步（改代码时必须执行）

文档过期是本仓库的历史顽疾。**功能性改动落在下表左列时，必须在同一个变更内同步右列文档**；拿不准就全量跑一次 `/docs-sync` 技能（每周六 10:00 定时自动化也会跑）。

| 代码区域 | 必须同步的文档 |
|---|---|
| 视频播放器/无缝循环（FfmpegVideoBackdropPlayer、SeamAnalyzer、PrerollHandoff） | docs/ARCHITECTURE.md §3.7 |
| 设置模型与校验（AppSettings、GameCatalogService） | docs/GAME_CONFIG.md settings 表 + §5 校验规则 |
| 配置模板（samples/games.json） | docs/GAME_CONFIG.md + README 快速开始 |
| UI 结构/色值/控件常数（App.axaml、MainWindow.axaml） | 本文件"主窗口结构速查" + skills avalonia-ui、desktop-ui-design |
| 新增/删除源文件 | docs/DEVELOPMENT.md §2 目录职责 |
| 新功能/测试数/常用命令 | README 功能表与测试节、docs/DEVELOPMENT.md §2 |
| 测试基建与坑 | skills avalonia-headless-testing |

三条纪律：

1. **点值必须标注**：往文档写数量/色值/尺寸/清单这类快照时，要么带"实测日期"（如"2026-09 实测"），要么改成可推导命令；能不写快照就不写。
2. **单一事实源**：同一事实只在一处权威定义，其余位置链接过去（历史教训：README 与 GAME_CONFIG 各写一份且互相矛盾）。
3. **流程性描述跟代码走**：时序图、线程模型、抽象表这类描述行为的段落，改动对应代码时当作代码的一部分一起改，不留"以后再说"。

## 分层边界

- `Core`（领域层）：零 UI 依赖、零厂商依赖；抽象在 `Abstractions/`，渠道只看 `IGameChannelApi`。
- `Channels.Kuro`（鸣潮，增量+HDiffPatch）/ `Channels.Hypergryph`（终末地，包式协议）：只依赖 Core 抽象。
- `YetAnotherGameLauncher`（App）：Avalonia UI（MVVM），DI 组合根在 `App.axaml.cs`；不直接引用渠道具体类型。
- 测试共享替身在 `tests/YetAnotherGameLauncher.TestSupport/`（FakeDownloader/FakeChannel/StubHttpHandler 等），新测试优先复用。

## 代码约定

- 库项目（Core/Channels.*）由各自 `.editorconfig` 强制 **CA2007**：所有 await 必须 `ConfigureAwait(false)`；App（UI 层）不加。
- 包版本集中在 `Directory.Packages.props`（CPM），新增包不改 csproj 里的 Version。
- XML 注释用中文，每个成员都有；每个函数有标准 `/// <summary>`。
- 提交信息：英文 conventional 风格（`feat(ui): ...`）。
- 本地化文案在 `src/YetAnotherGameLauncher/Resources/strings_*.json`（文件名用下划线）；主题资源在 `App.axaml` ThemeDictionaries，键以 `App` 前缀，**亮暗必须成对新增**。
- 默认配置模板单一来源是 `samples/games.json`（嵌入为 `games.sample.json`）。

## UI / 无头测试已知坑（实踩）

- **Avalonia headless 不执行动画**：样式动画与代码 `Animation.RunAsync` 都冻结在首帧（`ForceRenderTimerTick`/真实等待均无效）。模式：先写最终基值再播动画；需要测试落位时用 internal 开关跳过动画（现有先例：`MainWindow.NavIndicatorAnimationEnabled`，经 `InternalsVisibleTo` 暴露）。动画观感只能真机验证或截图 + judge。
- **Transform 上不能写 `x:Name`**（AVLN2000）；该错误还可能让后续增量构建产出缺预编译 XAML 的程序集（运行时报 "No precompiled XAML"）——见到此错误先清 bin/obj 全量重建。
- 代码构建的变换动画：`RunAsync` 目标必须是控件（Visual），keyframe 属性写 `TranslateTransform.YProperty`/`ScaleTransform.ScaleYProperty`，Avalonia 12 keyframe 缓动用 `KeySpline`（没有 `Easing` 属性）。
- **Fluent 主题的状态样式在模板 presenter 层写前景**：`Button` 的 `:pointerover`/`:pressed`/`:disabled` 把主题前景直接设在 `ContentPresenter#PART_ContentPresenter` 上，会压过 Button 本体的任何 Foreground（含继承）。自定义按钮的固定前景必须同样下沉到 presenter 层逐状态覆盖（先例：`Button.glass-onart` 组，见 MainWindow.axaml）。
- **`Image` 的 `UniformToFill` 默认按控件对齐居中裁切**：需要保住某一边（如海报左缘完整贴侧栏）时，设 `HorizontalAlignment="Left"` + `VerticalAlignment="Top"`，让测量出的封面尺寸向右/下溢出，由外层 `ClipToBounds` 裁掉。
- XAML 全部启用编译绑定：视图根必须有 `x:DataType`；绑定错误是编译错误，不要绕过。
- headless 换页后模板在下轮布局构建：切页后需 `window.UpdateLayout()`；落位类排队任务用 `Dispatcher.UIThread.RunJobs()` 冲刷。
- 视觉自检：`UiScreenshotTests.Export_UiScreenshots_ForReview` 导出 11 张截图到 `artifacts/ui-review/`（已 gitignore），改动 UI 后重跑并人工/judge 审查（`Export_LaunchErrorOverlay_ForReview` 导出启动失败覆盖层与 umu 引导提示）。
- 主窗口结构速查：标题色带 `TitleChrome` 高 56（46 可见 + 10px 延伸到内容圆角后方，与侧栏同色）+ 页面容器 `ContentCard`（挂 `content-card` 样式：所有页面统一全出血 + 左上 10px 圆角，Margin 0,46,0,0；`detail` 类已不存在）；侧栏选中指示点几何/编舞集中在 `MainWindow.axaml.cs`（`DotHeight` 等常量与 `BuildTransferCues`）；启动失败覆盖层挂详情页模板末尾（`GameItemViewModel.LaunchError` 驱动）；设置页启动卡带 `x:Name="LaunchCard"`（截图导出 BringIntoView 用）。
- **状态胶囊/长文案必须限宽换行**：详情页状态胶囊 `MaxWidth=640` + StatusText `MaxWidth=430 TextWrapping=Wrap`——启动预检的可操作提示很长，不设防会横穿窗口被裁（judge 实锤）。
- **ComboBox 的 SelectedItem 按引用匹配**：从枚举"解析"出的选项若不是 `ItemsSource` 集合内的实例，下拉框显示空白（`LaunchSettingsViewModel.DetectLaunchMode` 返回 `LaunchModes.First(...)` 即此故）。
- **headless 会话 `Dispatch` 只收 `Action`**（async lambda 即 async void），且不在 Dispatch 期间泵异步续体：服务内部 `await HttpClient` 之类的真异步调用挂进去会永久卡死。非 UI 的异步服务调用在会话启动（`HeadlessSession.Instance` 触发全局 locator 初始化）后**测试线程直调**即可（先例：`BackgroundResilienceTests`）；要碰 UI 对象才进 Dispatch。

## 其他坑

- 应用运行中会锁住 `bin` 下的 DLL，构建报 MSB3021/3027——先 `taskkill //F //IM YetAnotherGameLauncher.exe` 再构建；构建输出用 `tail` 截断时容易漏看这类错误。
- Windows 路径/注册表相关逻辑（自启动等）注意跨平台分支（`OperatingSystem.IsWindows()`），Linux 走 XDG autostart、Proton 兼容。
- 本机 Git Bash 的 `grep` 实为 **ugrep**：从仓库根递归扫描可能静默失败（报错被 `2>/dev/null` 吞掉后表现为"零匹配"）。查 tracked 内容用 `git grep`，全盘扫描用 `find ... | xargs` 交叉验证。
- FFmpeg 硬解回读 `av_hwframe_transfer_data` **不拷贝帧属性**：回读后软帧的 pts 全部退化为 0，必须在回读前从原始解码帧捕获 pts（`FfmpegVideoBackdropPlayer.TryDecodeNextSoftFrame`）。`av_frame_ref` 则会拷贝属性。
- 本机 BtbN FFmpeg n9.0 构建的 mov demuxer 上 `av_seek_frame` 后 `av_read_frame` 会提前 `AVERROR_EOF`（新开 demuxer 或非 EOF 状态都可能），且 seek 返回 0 不报错。背景视频循环一律**顺序读取 + 帧丢弃对齐**，禁止带时间戳的 seek（`FfmpegVideoBackdropPlayer` 的循环点对齐即此方案）。
- **Avalonia 12 没有 Wayland 后端**：整个包体系零 Wayland 痕迹，Linux 下一律走 X11（Wayland 会话即 XWayland）。Linux 渲染选项显式 EGL 优先（`X11PlatformOptions.RenderingMode = [Egl, Glx, Software]`，GLX 在 XWayland+NVIDIA 下是糊化/撕裂高发点），见 `Program.BuildAvaloniaApp`。
- **XWayland 拿不到合成器分数缩放**（X 服务器恒报 96dpi）：4K+1.67 的 Hyprland 桌面上整个 UI 按物理像素渲染——小字且糊。`Program.TrySyncXftDpiWithCompositor` 启动时经 hyprctl 把缩放写进 `Xft.dpi`（仅在用户未设置时；Avalonia 12 已无 `AVALONIA_SCREEN_SCALE_FACTORS` 环境变量）。
- **Hyprland 等合成器会无视 `WindowDecorations="BorderOnly"` 给 X11 窗口画 SSD 标题条**：Linux 下代码后置须在 `InitializeComponent()` **之后**设 `WindowDecorations.None`（XAML 属性会在初始化时覆盖构造函数先写的值）。
- **Linux 字体回退链必须以实际存在的 CJK 黑体开头**：只写 `Microsoft YaHei UI` 时 fontconfig 模糊匹配会落到楷体/宋体等衬线体，正文全变形（本机即无 Noto CJK、只有思源黑体）。窗口 FontFamily 与 `FontManagerOptions.DefaultFamilyName`（Program.cs）两处保持一致。
- **FFmpeg 系统库探测 Linux 上必须带 so 版本号**：`dlopen("avcodec")`（无 lib 前缀/版本号）与 Windows 名 `libavcodec-63.dll` 在 Linux 上永远失败；且只能加载与绑定精确配套的主版本（AutoGen 9.0 ↔ `libavcodec.so.63`），错版本结构体布局不配会直接崩（`FfmpegLibraryResolver`）。
- **SystemProcessRunner 即启即走 + 输出日志三坑**：① `BeginOutputReadLine` 事件会丢 stderr——`WaitForExit()` 只排空 stdout，必须自管 ReadLine 泵到 EOF；② `using var process` 在方法返回即 Dispose，会掐断管道，日志模式须泵收尾后再释放句柄；③ 进程可能在 `EnableRaisingEvents=true` 布防前退出，此时 Exited 永不触发，布防后要补查 `HasExited`（`SystemProcessRunner`）。
- 安装同步清理游离文件时 **prefix/compatdata 在保留名单**（`GameInstallService.PreservedEntries`）：Wine prefix 里有注册表/着色器缓存/用户数据，被清单外清理删掉等于毁掉游戏环境；Wine prefix 统一放 `~/.local/share/yagl/prefixes/<游戏id>`，绝不写进安装目录。
