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
- 视觉自检：`UiScreenshotTests.Export_UiScreenshots_ForReview` 导出 16 张截图（01-15 + 02b，2026-09-16 实测）到 `artifacts/ui-review/`（已 gitignore），改动 UI 后重跑并人工/judge 审查（`Export_LaunchErrorOverlay_ForReview` 导出启动失败覆盖层与 Linux 启动设置卡：umu 启动 + Proton 发行版下拉 + 检查更新按钮；`Export_ProtonUpdateConfirm_ForReview` 导出 Proton 更新确认覆盖层，内含纱罩压暗的像素级回归断言）。
- **视觉判定以像素级/字节级为准，图像分析工具会误报**：2026-09 实锤 analyze_image 对同一截图连续两次误称"纱罩未压暗"，纯红探针实验也误报"无红色"，而解帧字节证明纱罩/纯红一直正常渲染。颜色/遮罩类结论用帧 Lock 读像素或 PNG 解码采样交叉验证（先例：`Export_ProtonUpdateConfirm_ForReview` 的 LuminanceAt 断言）。
- 主窗口结构速查：标题色带 `TitleChrome` 高 56（46 可见 + 10px 延伸到内容圆角后方，与侧栏同色）+ 页面容器 `ContentCard`（挂 `content-card` 样式：所有页面统一全出血 + 左上 10px 圆角，Margin 0,46,0,0；`detail` 类已不存在）；内容卡左上圆角是侧栏与内容卡之间的**内部角**（色带垫色在圆弧缺口后），最大化也保留——`.maximized` 去圆角仅窗口外缘两角（侧栏左上/色带右上，贴屏幕边）；侧栏选中指示点几何/编舞集中在 `MainWindow.axaml.cs`（`DotHeight` 等常量与 `BuildTransferCues`）；**整页与详情页大块均为 Controls/ 下的 UserControl**（2026-09 提取：AboutPage/GachaPage/SettingsPage/GameSettingsPage/DetailActionDock/LaunchErrorOverlay，MainWindow 只留窗口骨架+侧栏+详情页背景层，DataTemplate 一行引用；提取约束：窗口骨架的具名元素不能动——`FindControl` 测试依赖 window namescope，UserControl 内名字只有视觉树搜索可见）；启动失败覆盖层 = `LaunchErrorOverlay` 控件挂详情页 Panel 末尾（`GameItemViewModel.LaunchError` 驱动）；游戏设置页启动卡带 `x:Name="LaunchCard"`（截图导出 BringIntoView 用）：Linux 启动方式二选一（umu 启动/直接运行），umu 模式旁为 Proton 发行版下拉（DW/GE/UMU-Proton，代号写入 `PROTONPATH` **并即时保存**——空配置兜底 DW-Proton、绝不回退 UMU-Proton，本地已装即用不联网拉 latest，`LaunchSettingsViewModel.ProtonFlavors`）；组件状态行下两个按钮：「检查/下载兼容组件」与「检查更新」（检测到新版变"更新到 {tag}"，`ProtonUpdateCheckState` 状态机驱动）；确认更新走**页内确认覆盖层**（GameSettingsPage 根 Grid 内：纱罩 `AppOverlayScrimBrush` + `launch-error-card` 卡，卡片 `MinWidth=340` 防版本号 token 被拆行；确认后 `UpdateProtonAsync` 装新版并清理同发行版旧目录）；**启动设置卡保存保留 `launch.umuId`**（SaveAsync 重建 LaunchOptions 时必须回填，否则 UMU_ID 退化为 umu-{gameId}——路由测试实锤）；**设置项实际变更落盘后弹轻提示**（SaveAsync 对模板/工作目录/环境变量与旧值逐一快照对比，仅有变更才弹并列出变更字段——仅 `PROTONPATH` 变化即发行版切换，按"Proton 发行版"提示而非"环境变量"；无变更/校验失败不弹，失败仍走页内消息槽；服务器下拉切换也弹；走 `GameItemViewModel.SettingsToastRequested` 事件转发，与状态 toast 同管线）。
- **详情页顶部信息簇（2026-09-16 方案 A「沉浸影院」落地）**：内容 Grid Margin `16,36,16,14`（操作坞通栏贴边：左右 16/距底 14），Row0 为左对齐簇（簇左缩进 12 = 距内容卡左缘 28px）——校验修复确认条 → chips 行（`Border.onart-chip` 胶囊：状态点+StatusText、已暂存徽章、版本 chip 四段 `VersionChipLead/Number/Mid/Target`，金色数字走 `AppVersionChipAccent` 暗色 #FFC861/亮色 #A96E10 成对）。簇后有一条高 150 的全出血顶部渐变纱带 `AppOnArtworkScrimBrush`（主题无关，IsHitTestVisible=False）。旧"页中上方居中合并胶囊"与"标题上墙"（DisplayName 30px + `DetailMetaText` 元信息行）已删除（2026-09-16 起仅留 chips 簇，`DetailMetaText`/`detail_meta_*` 键一并移除）；暗色 accent 为 #3D7DFF（方案 A 令牌，hover #2E68E0），NavIndicator 辉光同色；toast 卡用专属近实心底 `AppToastBackground`（亮暗成对）+ 阴影 0 8 24 #59000000；操作坞底色 `AppOnArtworkDockBrush` + 1px `AppOnArtworkCardBorder` 描边、标签 `AppOnArtworkTertiary` 11px、值列必须显式 `AppOnArtworkBrush`（继承主题前景在亮色主题会黑字上黑底——judge 02 实锤）。
- **状态 chip/长文案必须限宽换行**：详情页顶部 chips 行为 `WrapPanel`（`MaxWidth=640`、`ItemSpacing/LineSpacing=10`——放不下自动换行而非溢出；水平 StackPanel 会原样溢出，窄窗口下被内容卡裁掉）+ 状态 chip 内 StatusText `MaxWidth=430 TextWrapping=Wrap`——启动预检的可操作提示很长，不设防会横穿窗口被裁（judge 实锤；2026-09-16 起旧合并胶囊改为顶部 chips 簇，防线沿用）。
- **ComboBox 的 SelectedItem 按引用匹配**：从枚举"解析"出的选项若不是 `ItemsSource` 集合内的实例，下拉框显示空白（`LaunchSettingsViewModel.DetectLaunchMode` 返回 `LaunchModes.First(...)` 即此故）。
- **headless 会话 `Dispatch` 的 `Action` 重载不泵异步续体**（async lambda 即 async void）：服务内部 `await HttpClient` 之类的真异步调用挂进去会永久卡死。12.1.2 起另有 `Dispatch(Func<Task>)` 可等待重载且不被此坑（`StartupAssetPreloadTests`/`SettingsHeadlessTests` 在用）；非 UI 的异步服务调用在会话启动（`HeadlessSession.Instance` 触发全局 locator 初始化）后**测试线程直调**即可（先例：`BackgroundResilienceTests`）；要碰 UI 对象才进 Dispatch。
- **headless 平台窗口只在 `Show()` 前接受 `Width/Height`，且设 `WindowState=Maximized` 不会自动铺满**（真合成器会铺满工作区，headless 不会）：测最大化相关视觉（图标/圆角）须构造时按 `Screens.ScreenFromWindow` 的工作区定尺寸再 `Show`（先例：`SidebarNavHeadlessTests.CustomTitleBar_ButtonsPresent_AndMaximizeIconToggles`；`MainWindow.axaml` 写死了 `Width="1464" Height="720"`，不覆盖就会用默认尺寸）。
- **测窄窗口布局必须连 `MinWidth` 一起解除，并断言目标行为实际发生**：`MainWindow.axaml` 还写死了 `MinWidth="920"`，设 Width 低于它会被钳回 920，且 920 恰好触发侧栏自动收起（内容区反而变 852px）——两股力叠加后，想测的"放不下的窄布局"可能根本不存在，测试对旧代码假绿（2026-09-16 实锤：chips 行换行测试设 860 被钳回 920，最长行 790px 在收起态内容区里放得下，对修复前的 StackPanel 代码照样绿）。先例：`GameDetailPage_ChipsRow_LongStatus_WrapsInsteadOfClipping`（`MinWidth = 0` + `Width = 640` 构造，断言"版本 chip 换到状态 chip 下一行"这个行为本身，而非只断言"不越界"）。
- **`IsHitTestVisible=False` 在 Avalonia 会把整棵子树剪出命中测试**（与 WPF 不同，子级设回 `True` 也翻不回来）：ToastHost 宿主曾在 ItemsControl 上设 `IsHitTestVisible="False"` 想"面板穿透、卡片设回 True"，结果所有 toast 的关闭钮都点不动——点击直接穿透到下层页面，4s 自灭掩盖了症状（2026-09-17 实锤）。穿透靠"无背景（null）不参与命中"的默认语义即达（宿主 Right/Top 对齐、尺寸贴合卡片；对照先例：`TitleDrag` 需显式 `Background="Transparent"` 才可命中）。回归必须走真实指针：`ToastHeadlessTests.ClickingCloseButton_RemovesToast_ViaRealHitTesting` 用 `MouseMove/MouseDown/MouseUp` 走命中链路——直接 `DismissCommand.Execute` 的 VM 层测试（`ToastTests.DismissCommand_RemovesToast`）拦不住这类视图层断裂。

## 其他坑

- 应用运行中会锁住 `bin` 下的 DLL，构建报 MSB3021/3027——先 `taskkill //F //IM YetAnotherGameLauncher.exe` 再构建；构建输出用 `tail` 截断时容易漏看这类错误。
- Windows 路径/注册表相关逻辑（自启动等）注意跨平台分支（`OperatingSystem.IsWindows()`），Linux 走 XDG autostart、Proton 兼容。
- 本机 Git Bash 的 `grep` 实为 **ugrep**：从仓库根递归扫描可能静默失败（报错被 `2>/dev/null` 吞掉后表现为"零匹配"）。查 tracked 内容用 `git grep`，全盘扫描用 `find ... | xargs` 交叉验证。
- FFmpeg 硬解回读 `av_hwframe_transfer_data` **不拷贝帧属性**：回读后软帧的 pts 全部退化为 0，必须在回读前从原始解码帧捕获 pts（`FfmpegVideoBackdropPlayer.TryDecodeNextSoftFrame`）。`av_frame_ref` 则会拷贝属性。
- 本机 BtbN FFmpeg n9.0 构建的 mov demuxer 上 `av_seek_frame` 后 `av_read_frame` 会提前 `AVERROR_EOF`（新开 demuxer 或非 EOF 状态都可能），且 seek 返回 0 不报错。背景视频循环一律**顺序读取 + 帧丢弃对齐**，禁止带时间戳的 seek（`FfmpegVideoBackdropPlayer` 的循环点对齐即此方案）。
- **Linux 窗口后端：原生 Wayland 优先（Avalonia 12.1 实验性）+ X11/XWayland 回退**：Avalonia 12.1.0 起提供原生 Wayland 后端（独立 `Avalonia.Wayland` 包 + `UseWayland()` 显式启用；`UsePlatformDetect()` 不会自动选中且**无自动回退**——无 Wayland 合成器时直接启动失败，故必须条件启用）。决策在 `Services/WaylandBackendPolicy`（纯函数，含决策表测试）：Linux 且 `WAYLAND_DISPLAY` 非空即原生 Wayland，`YAGL_FORCE_XWAYLAND=1`（或 true）逃生舱回退 X11。X11 路径渲染选项仍显式 EGL 优先（`X11PlatformOptions.RenderingMode = [Egl, Glx, Software]`，GLX 在 XWayland+NVIDIA 下是糊化/撕裂高发点），见 `Program.BuildAvaloniaApp(bool)`。已知差异：Wayland 后端窗口 class/app_id 为空（`hyprctl clients` 的 class 是空串，Hyprland 窗口规则匹配不到），X11 路径 class 正常；NVIDIA 渲染异常时先试 `WaylandPlatformOptions.UseDmabufSwapchain = false`。**该后端还会把合成器平铺状态误报为 `WindowState.Maximized`**（2026-09 实测：Hyprland 平铺窗口 2516×1352、工作区 2560×1440 仍报 Maximized），且 `Screen.WorkingArea` 按 DIP 上报、`Scaling` 恒报 1（`PixelRect` 契约本应物理像素）——叠加用户合成器 `suppress_event=maximize`（忽略应用最大化请求，X11 有状态回报能自愈、Wayland 后端无）会让 `.maximized` 去圆角样式打在平铺/浮动窗口上，**详情页左上圆角丢失即此故**。修复：`Services/WindowStateMapper` 视觉最大化判定（Maximized 且客户区铺满工作区 ±4px，DIP/物理双单位候选），`MainWindow.UpdateMaximizedFlag` 在状态与尺寸变化两处时机重判，关闭时按视觉最大化持久化（决策表测试见 App.Tests）。
- **XWayland 拿不到合成器分数缩放**（X 服务器恒报 96dpi）：4K+1.67 的 Hyprland 桌面上整个 UI 按物理像素渲染——小字且糊。`Program.TrySyncXftDpiWithCompositor` 启动时经 hyprctl 把缩放写进 `Xft.dpi`（仅在用户未设置时；Avalonia 12 已无 `AVALONIA_SCREEN_SCALE_FACTORS` 环境变量）。**仅 X11/XWayland 路径执行**（原生 Wayland 由合成器直供分数缩放，无需也不应改写会话级 X 资源）。
- **Hyprland 等合成器会无视 `WindowDecorations="BorderOnly"` 给 X11 窗口画 SSD 标题条**：Linux 下代码后置须在 `InitializeComponent()` **之后**设 `WindowDecorations.None`（XAML 属性会在初始化时覆盖构造函数先写的值）。该后置写法对原生 Wayland 路径同样正确（2026-09 实测：CSD 生效、无双标题）。
- **Linux 字体回退链必须以实际存在的 CJK 黑体开头**：只写 `Microsoft YaHei UI` 时 fontconfig 模糊匹配会落到楷体/宋体等衬线体，正文全变形（本机即无 Noto CJK、只有思源黑体）。窗口 FontFamily 与 `FontManagerOptions.DefaultFamilyName`（Program.cs）两处保持一致。
- **FFmpeg 系统库探测 Linux 上必须带 so 版本号**：`dlopen("avcodec")`（无 lib 前缀/版本号）与 Windows 名 `libavcodec-63.dll` 在 Linux 上永远失败；且只能加载与绑定精确配套的主版本（AutoGen 9.0 ↔ `libavcodec.so.63`），错版本结构体布局不配会直接崩（`FfmpegLibraryResolver`）。
- **IDE0005（未使用 using）有两类与肉眼相左的特例**（2026-09 实锤各一例）：仅提供扩展方法的 using 构建期**不报**但可能必需（`FfmpegLibraryResolver` 的 `SharpCompress.Common`——用户删后编译/测试/运行全过，说明那条其实可删；判据只有"删掉重编译"）；反之为必需 using 但肉眼看似未用（cref/扩展方法解析）。结论：删 using 以"删后编译"为准，IDE0005 的沉默不是充分证据。
- **共享背景视频播放器（DI 单例）切游戏必须"停止即清帧"**：`Stop` 契约 = 取消循环 + 清空帧缓冲（`FfmpegVideoBackdropPlayer.StopCore`），配合 `PresentFrame` 代际门与 `GameItemViewModel` 的 `Frame 非空才点亮`——缺任何一层，上一游戏最后一帧的迟到通知（解码线程经 UI Dispatcher 异步投递）会让新详情页短暂显示旧画面（详见解法：ARCHITECTURE.md §3.7"停止即清帧"；回归：`VideoSource_SwitchingGames_LateStaleNotifyDoesNotLightNewGame`）。
- **SystemProcessRunner 即启即走 + 输出日志三坑**：① `BeginOutputReadLine` 事件会丢 stderr——`WaitForExit()` 只排空 stdout，必须自管 ReadLine 泵到 EOF；② `using var process` 在方法返回即 Dispose，会掐断管道，日志模式须泵收尾后再释放句柄；③ 进程可能在 `EnableRaisingEvents=true` 布防前退出，此时 Exited 永不触发，布防后要补查 `HasExited`（`SystemProcessRunner`）。
- 安装同步清理游离文件时 **prefix/compatdata 在保留名单**（`GameInstallService.PreservedEntries`）：Wine prefix 里有注册表/着色器缓存/用户数据，被清单外清理删掉等于毁掉游戏环境；Wine prefix 统一放 `~/.local/share/yagl/prefixes/<游戏id>`，绝不写进安装目录。
- **切 PATH 必须用 `Path.PathSeparator`，不能硬编码 `':'`**：Windows 上盘符 `C:` 会被切开，`SearchPath`/`FindOnPath` 返回缺盘符的相对根路径（CI Windows 腿红过）。同理，`GameLauncherService.ValidateCommand` 仅在 `pathValue is null && Windows` 时跳过裸命令预检——测试注入 `pathValue:""` 必须仍走 PATH 扫描，否则预检/错误覆盖层用例在 Windows 上会假绿成「已启动」。
- **测试里的路径断言两侧必须统一分隔符再比较**：期望值 `Path.Combine(...)` 在 Windows 产反斜杠、实际值常被归一成正斜杠，Linux CI 恰好两侧同斜杠掩盖问题（Windows CI 一次红 11 个）。两类实锤：①只归一实际值没归一期望值（`UmuComponentProvisionerTests`）；②期望值拼相对路径硬编码 `/` 而生产经 `GetFullPath` 产原生分隔符，或反之配置模板里的 `{installDir}/saves` 展开是**字面替换**不归一（`LaunchParameterRoundTripTests`）。比较前两侧都过 `Replace('\\', '/')`。
- **Windows 占用/只读语义是跨平台更新的头号杀手**（Linux `rename()`/`unlink()` 总能成功，问题只在 Windows 暴露）：被占用或只读的文件会让 `File.Move(overwrite:true)` 抛 IOException/UnauthorizedAccessException、让 `Directory.Delete(recursive:true)` 整体抛异常。统一防线（2026-09 全量修复）：原子写 `FileUtilities.WriteAtomicAsync`（解除只读+重试一次）、目录树 `FileUtilities.TryDeleteDirectory`（能删多少删多少）、下载落盘 `HttpFileDownloader.ReplaceDestination`（单独分类报"目标被占用"，绝不落进网络错误重试——重下多少遍都不会好）、背景落盘 `GameBackdropService` 换时间戳备用名。新增删除/覆盖代码先想这层。
- **`ZipFile.ExtractToDirectory` 在 Unix 把含 `\` 的 zip 条目名当字面文件名**（dotnet/runtime#98247，未修复）：Windows 打包器产出的包会在 Linux 解成安装根目录下的平铺垃圾文件，且"更新成功"。凡解包不可信外部 zip 必须手动遍历 `ZipArchive` 归一条目名（`PackageInstallerService.ExtractArchive`，顺带做 `..`/盘符穿越校验）。
- **Windows 的 `CreateProcess` 对裸命令名自动补 `.exe`，而 `File.Exists`/`IsExecutableFile` 不会**：预检 PATH 上的 "hpatchz" 时必须补试 `name + ".exe"`（`HpatchzApplier.ResolvePatchTool`），否则 Windows 误报工具缺失。Linux 还额外要求执行位（`FileUtilities.IsExecutableFile` 已含）。
- **`Process.StandardOutput.ReadToEnd()`（同步）会一直阻塞到子进程关闭 stdout，排在其后的 `WaitForExit(timeout)` 永远执行不到**——超时保护形同死代码，子进程挂起即卡死调用线程（实锤：`Program.cs` 启动期的 xrdb/hyprctl 查询曾会卡死 Main）。带超时的输出读取必须先 `ReadToEndAsync()` 再 `Wait(timeout)`，超时 Kill。
- **GitHub release 资产顺序 = 上传顺序，与架构无关**：GE-Proton11-6 曾把 aarch64 资产排在 x86_64 之前，"取第一个匹配的 tar"在 x86_64 主机上装出 ARM Proton（目录名还剥掉架构后缀看不出异常，其 toolmanifest 又会把 arm64 Steam Runtime 连带拖下来）。防御在 `UmuComponentProvisioner`：`SelectTarAsset` 按主机架构过滤资产名后缀（同架构优先、无后缀次之、反向排除，构造参数 `hostArchitecture` 可注入测试），解压后再读 `files/bin/wineserver` 的 ELF e_machine 兜底（0x3E=x86-64、0xB7=aarch64），不符即删目录报错；**本地解析同样过滤**（`FindInstalledProton` byName 与 `FindLatestLocalProton`），已装的错架构目录视同缺失、下次启动自动重装自愈（`MatchesHostArch`）。更新清理（`PruneOtherProtonVersions`）与安装共用 proton.lock；确认覆盖层提示先退出运行中的游戏（删旧版会让运行中游戏的延迟加载失效）。
