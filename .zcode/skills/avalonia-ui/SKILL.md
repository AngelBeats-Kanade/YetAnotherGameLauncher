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

- 主题相关刷子全部定义在 `App.axaml` 的 `ResourceDictionary.ThemeDictionaries`（`Light`/`Dark` 两个字典），键以 `App` 前缀命名（`AppAccentBrush`、`AppCardBackground`、`AppPageBackground`、`AppTextSecondary`、`AppErrorText`）。
- 亮暗主题**必须成对**新增键；暗色不用纯黑，亮色不用纯白（见 `desktop-ui-design`）。
- 切换主题：`Application.Current.RequestedThemeVariant = ThemeVariant.Default/Light/Dark`；跨线程设置需 `CheckAccess()`/`Dispatcher.Post`（见 `Themes/ThemeService.cs`）。
- 字体回退链写在中文字体上：`FontFamily="Microsoft YaHei UI, Noto Sans CJK SC, Segoe UI, Inter"`。

## 布局模式（本项目 MainWindow）

- 单窗口 + 左侧 `DockPanel` 侧栏（264px）+ `ContentControl` 主内容区。
- 页面切换：`ContentControl.Content` 绑定 VM 属性，用 `ContentControl.DataTemplates` + `x:DataType` 分发到各页面模板（无 NavigationView 依赖）。
- 列表选中态：`ListBox.SelectedItem` 双向绑定；条目模板内不放命令，操作集中在详情页。
- 内容区最大宽度 `MaxWidth=780` 居中可读性，`ScrollViewer` 包裹防溢出。

## 常见坑（本项目实踩）

1. xunit.v3 自动入口点生成命名空间会裁掉 `Tests` 尾词——测试项目命名空间不要以 `App` 等与主程序集类型同名的词结尾（CS0435）。
2. `Application.Current` 属于 UI 线程；后台线程改主题/控件需投递 Dispatcher。
3. `Progress<T>` 回调异步投递且**不保证顺序**；测试收集用 `ConcurrentQueue`（见 `avalonia-headless-testing`）。
4. Avalonia 12 的 `AvaloniaUI.DiagnosticsSupport` 包仅 Debug 有效，csproj 已条件化。
5. ComboBox 绑定枚举：`ItemsSource` 给 `IReadOnlyList<Enum>` 属性 + `SelectedItem` 双向绑定，配合 `JsonStringEnumConverter`。
6. 列表项 hover 高亮、按钮 accent 样式都走 `Classes` + 伪类选择器，不要内联触发器。

## 写完 UI 后必做

1. `dotnet build`（编译绑定错误在此暴露）。
2. 跑 `avalonia-ui-review` 的截图自检循环（渲染 → 看图 → 改 → 再渲染，亮暗两套）。
3. `dotnet test` 全绿。
