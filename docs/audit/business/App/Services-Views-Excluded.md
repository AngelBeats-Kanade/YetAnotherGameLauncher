# App/Services 其余 + 排除文件 + 视图层 逐行/逐段审计（2026-09-19）

## 非排除服务（有覆盖数据）

| 文件 | 判定 | 未覆盖行与处置 |
|---|---|---|
| BackgroundImageService.cs (213) | ✅ 逻辑 / ⚠️ 17 行 | 62-63（目录创建失败容错）、123-129（磁盘缓存写失败容错）、144-147/182-184/206-208（失败 TTL 过期清理/毒缓存失效的容错边角）。Phase 4：随 Phase 2 重构后的直调矩阵扩展 2-3 例 |
| LocalizationService.cs (87) | ✅ / ⚠️ 5 行 | 76-77/82-84（资源流缺失/坏 JSON 的回退容错——嵌入资源缺失为构建期错误，运行时理论不可达；变异裁决候选） |
| SeamAnalyzer.cs (165) | ✅ / ⚠️ 4 行 | 79-80/94-95（空输入/长度不匹配防御行）。Phase 4：2 个一行直调 |
| FrameSurface.cs (98) | ✅ / ⚠️ 15 行 | 23（Player 为 null 的 Render 守卫）、78-96（自由变换/拉伸分支——Render 覆盖层，仅真实渲染路径触达；VideoBackdropHeadlessTests 断言 Stretch/对齐属性而非 Render 调用本身）。Phase 4：Render 位图截取断言（headless CaptureRenderedFrame 像素采样，复用 LuminanceAt 手法） |
| WindowStateMapper.cs / WaylandBackendPolicy.cs / ThemeService.cs / LocBridge.cs / PrerollHandoff.cs | ✅ 100% 或 100%-1 | 决策表/纯逻辑全测 |

## 排除文件（[ExcludeFromCodeCoverage]，逐段结构性论证——维持排除的决定）

| 文件 | 行数 | 排除论证 | 可回收部分 |
|---|---:|---|---|
| FfmpegVideoBackdropPlayer.cs | 1421 | unsafe 原生 FFmpeg 互操作（av_* P/Invoke、帧缓冲指针运算、硬解回读）——单测无法在无原生库环境执行；**其全部纯逻辑已被提取为独立可测类型**（PlaybackClock 6 测试、DecodeGuard 6 测试、SeamAnalyzer 12 测试、PrerollHandoff 7 测试——这正是"提取后测"的设计证据） | 真机冒烟项（Phase 5 遗留清单）：FFmpeg 库缺失提示、硬解回退软解 |
| FfmpegLibraryResolver.cs | 364 | dlopen/注册表探测 + 精确主版本绑定（错版本 ABI 崩溃风险——AGENTS.md 记载）；纯逻辑部分（SHA256 校验 Sha256HexAsync 调用、路径候选） | Phase 4：路径候选函数提取或 internal 直测（约 30 行可回收） |
| Program.cs | 249 | 组合根与进程入口（BuildAvaloniaApp/Wayland 决策消费/xft dpi 同步/单实例互斥）；无返回值副作用代码，单测价值低 | WaylandBackendPolicy 纯函数已抽离全测；TrySyncXftDpiWithCompositor 的 hyprctl 输出解析若为纯函数可抽——Phase 4 检查（约 20 行可回收） |
| App.axaml.cs | 197 | DI 组合根（BuildServices 注册图）+ 生命周期回调；类型正确性由消费方全量测试背书（所有服务的构造契约都有测试） | 无（组合根豁免为业界常规，逐行论证保留） |
| FilePickerService.cs | 109 | StorageProvider 系统对话框封装（真实对话框不可 headless）；接口契约经 FakeFilePicker 全测 | 无 |
| WindowsPlatformInfo.cs | 23 | Windows 真机语义（注册表/进程探测），属性直读 | Windows CI 可达时移除排除属性即自动计量 |

## 视图层（Views/Controls + .axaml）

- MainWindow.axaml.cs (512)：79% 行覆盖，7 个 headless 测试族真实渲染驱动（Phase 1 已记录）；未覆盖行为窗口状态变更/拖拽边角——headless 可达性有限，Phase 4 视情况。
- 7 个页面 UserControl code-behind（各 12-28 行）：宿主渲染间接执行；**未覆盖行仅 GachaPage 4 行与 AppBackdrop 1 行**——Phase 4 随 GachaViewModel 组补。
- .axaml（11 文件）：按仓库纪律审——绑定正确性由编译绑定保证（AVLN 级）、主题资源亮暗成对由 ThemeHeadlessTests + 截图族守卫；不做伪行覆盖。UiScreenshotTests 的 16 张截图 + 像素断言为视觉证据链。

## 批次结论

排除决定全部给出结构性理由并经核可回收点（约 50 行可从排除文件回收）；
非排除服务无实锤 bug，容错行全部入 Phase 4 清单。
