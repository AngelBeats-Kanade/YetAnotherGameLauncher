---
name: avalonia-ui
description: Use when writing, modifying, or reviewing Avalonia UI code (.axaml, ViewModels, themes, styles, custom controls) in any Avalonia app — Avalonia UI 开发总纲。Load BEFORE touching any .axaml or Avalonia ViewModel code; routes to sibling skills for visual review, headless testing, and design rules.
---

# Avalonia UI 开发总纲

Avalonia 12（本项目 12.1.2）+ .NET 10。跨平台 XAML（.axaml）UI 框架，Skia 渲染。
**规则：在写或审查任何 Avalonia 代码前，先按下方路由表加载对应技能；UI 改动交付前必须跑过 `avalonia-ui-review` 的视觉自检循环。**

## 子技能路由表

| 任务 | 加载 |
|---|---|
| 交付/修改 UI 前的视觉自检与迭代 | `avalonia-ui-review` |
| 编写/修改 Avalonia 测试 | `avalonia-headless-testing` |
| 设计规范（间距/字体/颜色/暗色主题） | `desktop-ui-design` |
| 大改版/新页面：定美学方向、避免"AI 味"模板化设计 | `frontend-design`（移植自 Anthropic，先读其 README 的 Avalonia 适配注记） |
| 查设计数据库：风格/配色/字体搭配/UX 准则/Avalonia 栈规则 | `ui-ux-pro-max`（22 栈数据库，`--stack avalonia`；先读其 PORT-NOTES.md） |

**两种工作流的衔接**：大改版时先跑 `ui-ux-pro-max` 的 `--design-system` 得到风格/配色/字体方向，用 `frontend-design` 的"两遍法"（先出 token 方案→对照需求自检是否模板化→再动手）定稿，令牌落地到 `App.axaml` 的 ThemeDictionaries，然后按 `desktop-ui-design` + `avalonia-ui-review` 实现并截图验收。小改动跳过前两步，直接走实现+自检。

## 编译绑定（本项目启用 x:DataType）

- 每个 XAML 视图根元素声明 `x:DataType="vm:XxxViewModel"`；遗漏会导致运行时静默失败或编译错误。
- 编译绑定在**构建期**报错——把绑定错误当作编译错误处理，不要绕过。
- 绑定目标不在当前 DataContext 上的命令（如 `SettingsViewModel` 里的"返回"调用 `MainWindowViewModel` 的命令）：把命令暴露在当前 VM 上（转发），不要用 `$parent` 魔法路径。
- `ItemsSource` 模板内的 `{Binding}` 绑定的是**条目类型**，不是外层 VM。

## XAML 高频差异（相对 WPF）

| WPF | Avalonia |
|---|---|
| `Visibility="Collapsed"` | `IsVisible="False"`（bool，可绑定） |
| `Style.Triggers` | 伪类 `Selector:pointerover / :pressed / :focus` + `Classes.xxx` |
| `StaticResource` 跨主题 | 主题相关刷子一律 `DynamicResource`（配合 `ResourceDictionary.ThemeDictionaries`） |
| `CornerRadius` 仅 Border | Border 有 `CornerRadius`/`BoxShadow`；`ClipToBounds` 控制裁剪 |
| — | 伪类样式：`<Style Selector="Button.accent:pointerover /template/ ContentPresenter#PART_ContentPresenter">` |

## 主题资源（本项目约定）

- 主题相关刷子全部定义在 `App.axaml` 的 `ResourceDictionary.ThemeDictionaries`（`Light`/`Dark` 两个字典），键以 `App` 前缀命名（`AppAccentBrush`、`AppCardBackground`、`AppBackdropBaseBrush` + `AppBackdropGlowBrush`（窗口背景渐变与光晕）、`AppTextSecondary`、`AppErrorText`）。
- 亮暗主题**必须成对**新增键；暗色不用纯黑，亮色不用纯白（见 `desktop-ui-design`）。
- 切换主题：`Application.Current.RequestedThemeVariant = ThemeVariant.Default/Light/Dark`；跨线程设置需 `CheckAccess()`/`Dispatcher.Post`（见 `Themes/ThemeService.cs`）。
- 字体回退链写在中文字体上：`FontFamily="Microsoft YaHei UI, PingFang SC, Noto Sans CJK SC, Source Han Sans CN, Source Han Sans SC, WenQuanYi Zen Hei, Segoe UI, Inter"`（窗口 FontFamily 与 `Program.cs` 的 `FontManagerOptions.DefaultFamilyName` 两处保持一致）。**Linux 链上必须有发行版实际存在的黑体**（Noto CJK / 思源黑体）——只写 `Microsoft YaHei UI` 时 fontconfig 模糊匹配会落到楷体/宋体衬线体，正文全变形。

## Linux 渲染（实踩）

- **Linux 平台后端与窗口状态**（原生 Wayland 优先决策与 `YAGL_FORCE_XWAYLAND` 逃生舱 / EGL 优先 / Wayland 后端 Maximized 误报与视觉最大化判定 / Xft.dpi 分数缩放同步 / SSD 标题条与 `WindowDecorations.None` 后置）：单一事实源 docs/ARCHITECTURE.md §3.8——改 `Program.cs`/`WaylandBackendPolicy`/`WindowStateMapper` 前先读对应节。
- 长文案的状态 chip/提示条必须 `MaxWidth + TextWrapping`，否则会横穿窗口被裁（judge 实锤；详情页 chips 行整体限宽 640）。
- 压在深色玻璃底/插画上的文字必须显式插画上前景（`AppOnArtworkBrush` 系），继承主题前景在亮色主题会黑字叠黑底（judge 实锤：操作坞值列）。
- **BoxShadow 无头渲染正常、真机渲染管线（原生 Wayland + NVIDIA + HDR 输出）会呈成边缘生硬的灰色矩形板**（2026-09-17 实锤：toast 卡阴影在用户屏幕上是"卡片同尺寸的硬边灰板"；同刻 grim 抓帧里阴影却几乎不存在，headless 截图完全正常——三路互证）。弹层类 UI（toast/错误卡/修复确认条）的层次一律用「1px 描边 + 近实心底」表达，不要用 BoxShadow（NavIndicator 的 blur 8 小辉光是已知例外）。

## 布局模式（本项目 MainWindow）

- 单窗口 + 左侧 `Grid ColumnDefinitions="Auto,*"` 侧栏 + `ContentControl` 主内容区；侧栏宽度绑定
  `SidebarWidth`，动态 264↔68 可折叠（0.2s 宽度过渡），264 仅为展开值。
- 页面切换：`ContentControl.Content` 绑定 VM 属性，用 `ContentControl.DataTemplates` + `x:DataType` 分发到各页面模板（无 NavigationView 依赖）。
- 列表选中态：`ListBox.SelectedItem` 双向绑定；条目模板内不放命令，操作集中在详情页。
- 内容区各页最大宽度 `MaxWidth=720/760` 居中可读性，`ScrollViewer` 包裹防溢出。

## 常见坑（本项目实踩）

1. xunit.v3 自动入口点生成命名空间会裁掉 `Tests` 尾词——测试项目命名空间不要以 `App` 等与主程序集类型同名的词结尾（CS0435）。
2. `Application.Current` 属于 UI 线程；后台线程改主题/控件需投递 Dispatcher。
3. `Progress<T>` 回调异步投递且**不保证顺序**；测试收集用 `ConcurrentQueue`（见 `avalonia-headless-testing`）。
4. Avalonia 12 的 `AvaloniaUI.DiagnosticsSupport` 包仅 Debug 有效，csproj 已条件化。
5. ComboBox 绑定枚举：`ItemsSource` 给 `IReadOnlyList<Enum>` 属性 + `SelectedItem` 双向绑定，配合 `JsonStringEnumConverter`。**`SelectedItem` 按引用匹配**：从枚举"解析"出的选项若不是 `ItemsSource` 集合内的同一实例，下拉框显示空白（`LaunchSettingsViewModel.DetectLaunchMode` 返回 `LaunchModes.First(...)` 即此故）。
6. 列表项 hover 高亮、按钮 accent 样式都走 `Classes` + 伪类选择器，不要内联触发器。
7. **`IsHitTestVisible=False` 的视觉会被连整棵子树剪出命中测试**（与 WPF 不同，子级设回 `True` 翻不回来）：想要"宿主面板穿透、仅卡片可点"，靠宿主**无背景（null）不参与命中**的默认语义即达（无背景的控件不挡点击；需要可点时才显式 `Background="Transparent"`），不要在宿主上写显式 False——toast 关闭钮因此点不动过（2026-09-17 实锤；回归必须走真实指针，见 `avalonia-headless-testing`）。
8. **Transform 上不能写 `x:Name`**（AVLN2000）；该错误还可能让后续增量构建产出缺预编译 XAML 的程序集（运行时报 "No precompiled XAML"）——见到此错误先清 bin/obj 全量重建。
9. **Fluent 主题的状态样式在模板 presenter 层写前景**：`Button` 的 `:pointerover`/`:pressed`/`:disabled` 把主题前景直接设在 `ContentPresenter#PART_ContentPresenter` 上，会压过 Button 本体的任何 Foreground（含继承）。自定义按钮的固定前景必须同样下沉到 presenter 层逐状态覆盖（先例：`Button.glass-onart` 组，MainWindow.axaml）。
10. **`Image` 的 `UniformToFill` 默认按控件对齐居中裁切**：需要保住某一边（如海报左缘完整贴侧栏）时，设 `HorizontalAlignment="Left"` + `VerticalAlignment="Top"`，让测量出的封面尺寸向右/下溢出，由外层 `ClipToBounds` 裁掉。
11. **Animation API（2026-09-21 实测，本项目编舞已弃用之，改手写驱动——见 docs/UI_STRUCTURE.md §2.1）**：`RunAsync` 目标必须是控件（Visual）；keyframe 属性写 `TranslateTransform.YProperty`/`ScaleTransform.ScaleYProperty`；keyframe 缓动用 `KeySpline`（没有 `Easing` 属性），且 **KeySpline 作用于"进入该帧"的段落**（帧 i 的样条管 i-1→i 段）；一个 KeyFrame 带多个 Setter 与拆成多条单属性动画**功能等价**（真机 A/B 插桩实测，引擎源码 `Animation.InterpretKeyframes`/`TransformAnimator` 逐层核对）——不要用"两动画失步"解释卡顿。空闲渲染循环下 Animation 时钟无法自举的根因见 docs/ARCHITECTURE.md §3.7。

## 写完 UI 后必做

1. `dotnet build`（编译绑定错误在此暴露）。
2. 跑 `avalonia-ui-review` 的截图自检循环（渲染 → 看图 → 改 → 再渲染，亮暗两套）。
3. `dotnet test` 全绿。
