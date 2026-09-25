# UI 结构速查

主窗口/各页面的**结构快照与控件常数**单一事实源。改任何 .axaml/视觉相关代码前先读对应节；改动落在本文件范围时，**同一变更内同步本文件**（AGENTS.md「文档同步」表）。分工：设计规则（间距节奏/字号/颜色令牌/按钮层级）在 skills `desktop-ui-design`；Avalonia 通用机制与实坑在 skills `avalonia-ui`；视觉自检循环在 skills `avalonia-ui-review`；本文件只记"当前长什么样、常数是多少"。

快照值除另行标注外均为 2026-09 实测。回归测试名随条目给出，是这些事实的可执行钉。

## 1. 窗口骨架与页面容器

- 标题色带 `TitleChrome` 高 56：46 可见 + 10px 延伸到内容圆角后方，与侧栏同色。
- 页面容器 `ContentCard` 挂 `content-card` 样式：所有页面统一全出血 + 左上 10px 圆角，Margin 0,46,0,0（`detail` 类已不存在）。
- 内容卡左上圆角是侧栏与内容卡之间的**内部角**（色带垫色在圆弧缺口后），最大化也保留——`.maximized` 去圆角仅窗口外缘两角（侧栏左上/色带右上，贴屏幕边）。
- 窗口 `Title` 绑定 `MainWindowViewModel.WindowTitle`：游戏页"名 · 应用名"、其余页应用名，经 `OnCurrentPageChanged` 通知。自绘游戏名标题已移除（2026-09-25 用户决定），识别职责由窗口 Title 与侧栏选中项承担。
- **整页与详情页大块均为 `Controls/` 下的 UserControl**（2026-09 提取：AboutPage/GachaPage/SettingsPage/GameSettingsPage/DetailActionDock/LaunchErrorOverlay；MainWindow 只留窗口骨架+侧栏+详情页背景层，DataTemplate 一行引用）。提取约束：窗口骨架的具名元素不能动——`FindControl` 测试依赖 window namescope，UserControl 内名字只有视觉树搜索可见。
- XAML 引用 C# 常量须 `x:Static`，编译绑定不解析 const。

## 2. 侧栏与导航指示点

- 选中指示点几何/编舞集中在 `MainWindow.axaml.cs`（`DotHeight` 等常量与 `BuildTransferCues`）。两态同一"点在选中块内部左缘"语言：展开态内缩 3px；收起态贴块左缘 `CollapsedInsetX=0`——收起态块 44 宽、图标盒 38 居中后左缝仅 3px，点与图标盒左缘相切叠 2px。
- **高亮背景 = ContentPresenter 区域，不随 Item 负 Margin 溢出扩展（已实测）**。勿再尝试外扩块让点进块内——"点在块外"形态已被用户否决（2026-09-25 两轮返工）。回归 `SidebarNavHeadlessTests`。
- 设置/关于按钮的展开态 Padding/对齐必须走 `.nav-item` 样式而非按钮本地值——本地值会压过收缩覆盖样式，使收起态图标居中失效。
- 侧栏游戏项模板根带名称 ToolTip（收起态纯图标仍可辨识，评审 P2-6）。
- 侧栏状态行绑定 `SidebarStatusText` 短变体（`status_detected/notInstalled` 有 Short 键，EN 全文案 39 字符必截残句，评审 P2-11）。机制 = 状态机显式赋值短变体 + `OnStatusTextChanged` 同步其余瞬态全文；不用滞留覆盖机制（同值赋值不触发通知会滞留）。

### 2.1 指示点迁移动画：手写驱动契约（2026-09-21 起）

弃用 Animation API 的根因（渲染循环空闲降频、时钟无法自举）见 docs/ARCHITECTURE.md §3.7。驱动契约：

- 驱动 = `DriveTransferAsync` + 纯函数 `EvalTransfer`：钉边不变量/接缝连续性/缓动曲线全部可单测；`Task.Delay(8ms)` 循环自身就是渲染泵（每拍直写变换基值 → 失效 → 渲染）。
- 手写驱动直写基值，失去动画优先级层遮盖：迁移进行中 `MoveNavIndicator` **不得直写变换属性**（终态只入记录，由驱动独占写入），否则终态基值盖掉首拍飞行值——观感为"指示点先在目的地闪现再跳回"。
- 快速连点：旧驱动的收尾/异常复位必须以 `_indicatorCts` 所有权比对守卫，不得覆盖新驱动。
- 驱动末拍必须按实时几何收敛（再跑一次落位计算）：迁移起点几何带着按压态 `pressable` scale(0.97)（**参与 TranslatePoint**），飞行期间布局也可能微调——残差要在落地帧内吃掉，推迟补写即"动画结束后鼠标一动指示点又挪一下"。
- 收敛/复核判定必须比**自记录的基值**（`IndicatorTop` 等）：动画进行中读变换属性拿到的是动画优先级生效值，误比会自旋（曾实测 7 秒 14 万次）。
- 动画时间线按墙钟推进：UI 线程被重活占住时剩余时间轴压缩成大跳步——切游戏起播曾挤压编舞，现 `GameItemViewModel.VideoStartDeferral` 把起播挪出迁移编舞窗口。同类症状先怀疑 UI 线程阻塞（诊断手法：环境变量门控临时插桩 + Render 优先级采样器逐帧记录变换值 + 脚本化自动切换，跑完即删）。

## 3. 详情页（方案 A「沉浸影院」）

- 内容 Grid Margin `16,36,16,16`（操作坞通栏贴边：左右 16/距底 16，2026-09-23 刻度审计对齐 4pt）；Row0 为左对齐簇，簇左缩进 12 = 距内容卡左缘 28px。
- chips 行：`Border.onart-chip` 胶囊（状态点+StatusText、已暂存徽章、版本 chip 四段 `VersionChipLead/Number/Mid/Target`）；`WrapPanel` `MaxWidth=640`、`ItemSpacing/LineSpacing=8`（2026-09-23 对齐 4pt/2pt 网格，放不下自动换行）；状态 chip 内 StatusText `MaxWidth=430 TextWrapping=Wrap`——启动预检的长提示不设防会横穿窗口被裁（judge 实锤）。
- 版本 chip 金色数字走 `AppVersionChipAccent`：暗色 #FFC861 / 亮色 #8A5A0C 成对；chip 底暗 70%/亮 90% 不透明度。三者均受 `ThemeTokenContrastTests` 对比度门禁管辖（2026-09-23 达标）；门禁暗侧最坏背景钉住背板渐变最亮 stop `#131A2E`——改 `AppBackdropBaseBrush` 渐变需同步门禁口径。
- 顶部渐变纱带 `AppOnArtworkScrimBrush`：全出血**固定高 140**（chips 位置不随窗口缩放、固定高恰好覆盖；主题无关、IsHitTestVisible=False；2026-09-25 曾为压官方烧录 Logo 加深加高至 30% 比例，随标题移除回调本值）。回归 `GameDetailPage_Scrim_DimsArtworkBehindTitleCluster` 像素探针。
- 未安装空态引导卡 `EmptyStateCard`：`ShowEmptyState` = `!IsInstalled && !CanLaunch`——detected 态（可直接启动）不显示，避免安装引导与启动主钮同屏矛盾。回归 `DetailPageIdentityTests`（渠道/最新版本/服务器/安装目录 + 引导文案 `detail_emptyTitle/emptyHint`）。操作坞启动钮同步 `IsVisible=CanLaunch`，不再以禁用态常驻——隐藏的按钮模板不实例化，断言其 presenter 前景的旧测试已改探针承载。
- 色值：暗色 accent #2E68E0、亮色 #1E66D6（2026-09-23 经对比度门禁压暗：原 #3D7DFF/#2E7CF6 白字仅 3.77:1/3.94:1；hover 各再压一档），NavIndicator 辉光同色。操作坞底色 `AppOnArtworkDockBrush` + 1px `AppOnArtworkCardBorder` 描边；标签 `AppOnArtworkTertiary` 12px（#B6BCCB，2026-09-23 自 #8F95A6 提亮达标）；值列必须显式 `AppOnArtworkBrush`（继承主题前景在亮色主题会黑字上黑底——judge 实锤）。
- 旧"页中上方居中合并胶囊"与"标题上墙"（DisplayName 30px + `DetailMetaText` 元信息行）已删除（2026-09-16 起仅留 chips 簇，`detail_meta_*` 本地化键一并移除）。

## 4. 游戏设置页

- 启动卡带 `x:Name="LaunchCard"`（截图导出 BringIntoView 用）。Linux 启动方式二选一（umu 启动/直接运行）；umu 模式旁为 Proton 发行版下拉（DW/GE/UMU-Proton，代号写入 `PROTONPATH` **并即时保存**——空配置兜底 DW-Proton、绝不回退 UMU-Proton，本地已装即用不联网拉 latest，`LaunchSettingsViewModel.ProtonFlavors`）。
- 组件状态行下两个按钮：「检查/下载兼容组件」与「检查更新」（检测到新版变"更新到 {tag}"，`ProtonUpdateCheckState` 状态机驱动）。两者的结果/进度反馈写 `LaunchSettings.UmuFeedback` 消息槽、渲染在启动卡 umu 面板按钮行下方——位置卡的 `Save` 槽只承载保存流程，两者互不清理（曾混用致反馈显示到位置卡，2026-09-23 修复）。回归 `UmuFeedbackHeadlessTests`（视觉树归属）。
- 环境变量框下「保存启动设置」按钮（`launch_save` 键 → `LaunchSettings.SaveCommand`）：启动方式/命令模板/工作目录/环境变量是**草稿字段、无即时落盘入口**（Proton 下拉只覆盖发行版），此按钮与位置卡 `InstallDirBox` 回车（窗口级 Enter 处理器，`MainWindow.axaml.cs`）是它们的保存路径。按钮**随 `LaunchSettings.IsDirty` 点亮**（五个草稿字段经 RecomputeDirty 与已保存值同规则比对，保存/回滚后复位；Linux 首运推荐链写草稿即算脏——本就待用户保存）。回归 `InteractionConsistencyTests`。注意：Enter 处理器须从壳 `GameSettingsViewModel` 取 `LaunchSettings` 再分发——直接 `is LaunchSettingsViewModel` 永不匹配（页面 DataContext 是壳，2026-09 实锤修复）。
- **启动设置卡保存保留 `launch.umuId`**：SaveAsync 重建 LaunchOptions 时必须回填，否则 UMU_ID 退化为 umu-{gameId}（路由测试实锤）。
- 确认更新走**页内确认覆盖层**（GameSettingsPage 根 Grid 内：纱罩 `AppOverlayScrimBrush` + `launch-error-card` 卡；卡片 `MinWidth=340` 防版本号 token 被拆行；确认钮为 `danger-card` 红字红描边幽灵钮、文案"删除旧版并更新"——破坏性操作不与 accent 实心主钮共用样式；确认后 `UpdateProtonAsync` 装新版并清理同发行版旧目录）。回归 `DangerActionStyleTests`。
- 设置项实际变更落盘后弹轻提示：SaveAsync 对模板/工作目录/环境变量与旧值逐一快照对比，仅有变更才弹并列出变更字段（仅 `PROTONPATH` 变化即发行版切换，按"Proton 发行版"提示而非"环境变量"；无变更/校验失败不弹，失败仍走页内消息槽；服务器下拉切换也弹）。走 `GameItemViewModel.SettingsToastRequested` 事件转发，与状态 toast 同管线。

## 5. 覆盖层与浮动层

- 启动失败覆盖层 = `LaunchErrorOverlay` 控件挂详情页 Panel 末尾（`GameItemViewModel.LaunchError` 驱动）。重试/关闭主次随 `CanRetry` 经 Classes 条件绑定对调：可重试时"重试"是 `launch-error-primary`、"关闭"退居 `launch-error-secondary`，不可重试时反转且重试隐藏（评审 P1-4）。回归 `DangerActionStyleTests`。
- **toast 门控**：启动失败覆盖层在场（CurrentPage 为游戏页且 HasLaunchError）时 `ShowToast` 直接丢弃新 toast——瞬态消息不得压过模态错误，覆盖层退场自动恢复（评审 P3-13）。回归 `ReviewPolishTests`。toast 卡用专属近实心底 `AppToastBackground`（亮暗成对；层次靠 1px 描边，不用 BoxShadow，原因见 skills `avalonia-ui`）。
- 启动遮蔽层 `BootSplash`：主窗口根 Panel 末位，`IsBooting` 驱动，默认 false 不影响测试/截图；生产路径经 `BeginBootSplash` 开启、`RunBootGateAsync` 放行（放行矩阵/时序见 docs/ARCHITECTURE.md §3.7，纯决策 `BootGate.ShouldRelease`）；代码后置自泵脉动条 + 0.25s 淡出，`SplashAnimationEnabled` 测试开关；Transform 不能 x:Name，脉动条位移运行时挂。

## 6. 关于页

- 信息卡带项目主页出口：`AboutViewModel.OpenProjectHomeCommand` → `IPlatformInfo.OpenInBrowser`（xdg-open/shell 关联），URL 常量 `AboutViewModel.ProjectHomeUrl`（评审 P3-15）。按钮为 GitHub 官方 mark 16px 幽灵图标钮（`ghost-home` 类复用 icon-btn 幽灵底：常态辅助色、hover 显微亮底转 accent；完整 URL 收进 ToolTip）——带底描边胶囊/整串 URL 文字两版均被用户否决（信息卡纯文本行内唯一带底控件即"突兀"，2026-09-25 用户定稿图标形态）。
