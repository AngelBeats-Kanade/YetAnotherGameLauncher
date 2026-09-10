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
- 视觉自检：`UiScreenshotTests.Export_UiScreenshots_ForReview` 导出 8 张截图到 `artifacts/ui-review/`（已 gitignore），改动 UI 后重跑并人工/judge 审查。
- 主窗口结构速查：46px 标题色带（`TitleChrome`，与侧栏同色）+ 内容容器 `ContentCard`（透明底；详情页切 `detail` 类 = 全出血 + 左上圆角，设置类页保持 0,6,6,6 浮卡）；侧栏选中指示点几何/编舞集中在 `MainWindow.axaml.cs`（`DotHeight` 等常量与 `BuildTransferCues`）。

## 其他坑

- 应用运行中会锁住 `bin` 下的 DLL，构建报 MSB3021/3027——先 `taskkill //F //IM YetAnotherGameLauncher.exe` 再构建；构建输出用 `tail` 截断时容易漏看这类错误。
- Windows 路径/注册表相关逻辑（自启动等）注意跨平台分支（`OperatingSystem.IsWindows()`），Linux 走 XDG autostart、Proton 兼容。
- 本机 Git Bash 的 `grep` 实为 **ugrep**：从仓库根递归扫描可能静默失败（报错被 `2>/dev/null` 吞掉后表现为"零匹配"）。查 tracked 内容用 `git grep`，全盘扫描用 `find ... | xargs` 交叉验证。
- FFmpeg 硬解回读 `av_hwframe_transfer_data` **不拷贝帧属性**：回读后软帧的 pts 全部退化为 0，必须在回读前从原始解码帧捕获 pts（`FfmpegVideoBackdropPlayer.TryDecodeNextSoftFrame`）。`av_frame_ref` 则会拷贝属性。
- 本机 BtbN FFmpeg n9.0 构建的 mov demuxer 上 `av_seek_frame` 后 `av_read_frame` 会提前 `AVERROR_EOF`（新开 demuxer 或非 EOF 状态都可能），且 seek 返回 0 不报错。背景视频循环一律**顺序读取 + 帧丢弃对齐**，禁止带时间戳的 seek（`FfmpegVideoBackdropPlayer` 的循环点对齐即此方案）。
