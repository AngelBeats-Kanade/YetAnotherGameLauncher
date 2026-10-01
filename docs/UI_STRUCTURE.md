# UI 结构速查

主窗口/各页面的**结构快照与控件常数**单一事实源。改任何 .axaml/视觉相关代码前先读对应节；改动落在本文件范围时，**同一变更内同步本文件**（AGENTS.md「文档同步」表）。分工：设计规则（间距节奏/字号/颜色令牌/按钮层级）在 skills `desktop-ui-design`；Avalonia 通用机制与实坑在 skills `avalonia-ui`；视觉自检循环在 skills `avalonia-ui-review`；本文件只记"当前长什么样、常数是多少"。

快照值除另行标注外均为 2026-09 实测。回归测试名随条目给出，是这些事实的可执行钉。

## 1. 窗口骨架与页面容器

- 标题色带 `TitleChrome` 高 56：46 可见 + 10px 延伸到内容圆角后方，与侧栏同色。
- 页面容器 `ContentCard` 挂 `content-card` 样式：所有页面统一全出血 + 左上 10px 圆角，Margin 0,46,0,0（`detail` 类已不存在）。
- 内容卡左上圆角是侧栏与内容卡之间的**内部角**（色带垫色在圆弧缺口后），最大化也保留——`.maximized` 去圆角仅窗口外缘两角（侧栏左上/色带右上，贴屏幕边）。
- 窗口 `Title` 绑定 `MainWindowViewModel.WindowTitle`：游戏页"名 · 应用名"、其余页应用名，经 `OnCurrentPageChanged` 通知。自绘游戏名标题已移除（2026-09-25 用户决定），识别职责由窗口 Title 与侧栏选中项承担。
- **整页与详情页大块均为 `Controls/` 下的 UserControl**（2026-09 提取：AboutPage/GachaPage/SettingsPage/GameSettingsPage/DetailActionDock/LaunchErrorOverlay；2026-09-29 增 GameDetailPage——MainWindow 内联的详情页模板整体迁出，五个页面模板全部一行引用，MainWindow 只留窗口骨架+侧栏）。提取约束：窗口骨架的具名元素不能动——`FindControl` 测试依赖 window namescope，UserControl 内名字只有视觉树搜索可见（GameDetailPage 的 PosterImage/ArtworkFallback/EmptyStateCard 全部走视觉树断言）。
- **MainWindow 样式库在 `Themes/WindowStyles.axaml`**（2026-09-29 自 Window.Styles 迁出，经 `StyleInclude` 引用、作用域保持窗口级）：98 个选择器（按钮四态族/卡片/侧栏/覆盖层/导航动画等）；按钮/图标圆角 10 收敛为该字典 `Styles.Resources` 内的 `ButtonCornerRadius` 令牌（卡片/操作坞/chips 仍为字面 14）；四态按钮族的 presenter 下沉语义逐字保留（skills avalonia-ui 坑 9）。 MainWindow.axaml 只剩窗口骨架（291 行）。
- XAML 引用 C# 常量须 `x:Static`，编译绑定不解析 const。
- **应用背景层 `Controls/AppBackdrop`**（2026-09-29 补记）：四层 Panel——`AppBackdropBaseBrush` 主题渐变 → `AppBackdropGlowBrush` 光晕 → `AppBackdropGlowWarmBrush` 暖光晕（两光晕 Margin 由宿主 `GlowMargin` StyledProperty 决定，窗口骨架传 `0,46,0,0` 避开标题色带、页面板内缺省全出血）→ 自定义背景图 `UniformToFill`（`AppBackgroundImage` 非空时覆盖前二层）。窗口级与 BootSplash、页面板内各一枚。
- **窗口自绘标题三钮**（2026-09-29 补记）：右上 `StackPanel`（Margin 0,4,8,0）内 `CaptionMinimize`/`CaptionMaximize`/`CaptionClose` 三钮，`caption-btn` 样式 46×34 圆角 8、透明底、hover `AppSidebarHover`（close hover 红 `#E81123` + 白前景）；`MaximizeIcon`/`RestoreIcon` 两个 Path 的可见性由 `IsWindowMaximized` 驱动、tooltip 随态切换（`window_maximize`/`window_restore`）。x:Name 五枚被代码后置与 headless 测试按 window namescope `FindControl` 引用——**留在窗口文件内，不得提取为 UserControl**。

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

- 结构落点：`Controls/GameDetailPage.axaml`（2026-09-29 自 MainWindow 内联模板提取；x:DataType=GameItemViewModel）。本节常数与形态描述不变，回归测试引用同节。

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
- 环境变量框下「保存启动设置」按钮（`launch_save` 键 → `LaunchSettings.SaveCommand`）：启动方式/命令模板/工作目录/启动选项/环境变量是**草稿字段、无即时落盘入口**（Proton 下拉只覆盖发行版），此按钮与位置卡 `InstallDirBox` 回车（窗口级 Enter 处理器，`MainWindow.axaml.cs`）是它们的保存路径。按钮**随 `LaunchSettings.IsDirty` 点亮**（草稿字段经 RecomputeDirty 与已保存值同规则比对，保存/回滚后复位；Linux 首运推荐链写草稿即算脏——本就待用户保存）。回归 `InteractionConsistencyTests`。注意：Enter 处理器须从壳 `GameSettingsViewModel` 取 `LaunchSettings` 再分发——直接 `is LaunchSettingsViewModel` 永不匹配（页面 DataContext 是壳，2026-09 实锤修复）。
- **启动选项开关区（2026-09-28 增）**：环境变量框上方 `IsVisible=LaunchSettings.IsLinux` 的区块（`launch_options` caption + 三行 ToggleSwitch）：使用 Wayland（`launch_useWayland` → `LaunchSettings.UseWaylandDraft`）、升级 DLSS 模型（`UpgradeDlssDraft`）、打印 Proton 日志（`EnableProtonLogDraft`），行距 4/组距 8（4pt 网格）；每行开关下方 `card-caption` hint `HorizontalAlignment=Left + MaxWidth=420 Wrap`——**必须显式 Left：Avalonia 的 Stretch+MaxWidth 是约束居中而非左对齐**（hint 曾整体右移 130px，judge 实锤）。开关 On/Off 状态字置空（Fluent 默认渲染英文 On/Off）。开关翻转走草稿语义点亮保存钮，保存经 `CompatTools.FeatureEnvironment` 注入（映射见 docs/ARCHITECTURE.md §3.1）。回归 `LaunchOptionsHeadlessTests`（Windows 假平台隐藏/Linux 绑定草稿+点亮脏标）。
- **资源包档位下拉（2026-10-02 增）**：启动选项区之后、环境变量框之前的 `Spacing=4` 区块，`IsVisible={Binding Game.IsKuro}`（鸣潮专属，HasGachaEntry 门控先例；**不套 IsLinux**——游戏参数与平台无关）：caption（`launch_resourceQuality`）+ ComboBox（MinWidth=220 与启动方式下拉同宽，ItemsSource={Binding LaunchSettings.ResourceQualities}/SelectedItem=SelectedResourceQuality，选项 record 含本地化显示名、默认档在前）+ hint（card-caption，Left + MaxWidth=420 Wrap）。草稿语义（改选点亮保存钮），保存重建 LaunchOptions 时空档归一为 null 落盘；轻提示并入「启动选项」组。选项 hd/sd/uhd 以 `-krqlv=<tier>` 追加进两条启动链（模板直启链命令尾拼接 / umu 链 BuildEntryCommand argv 尾部）。回归 `LaunchOptionsHeadlessTests.ResourceQualityCombo_*` 与 `LaunchSettingsTests.ResourceQuality_*`。截图 11/11b（2026-10-02 judge 双 PASS）。
- **环境变量框只显示用户自定义键（2026-09-28 托管语义）**：推荐链生成/内置键（`CompatTools.IsGeneratedEnvironmentKey`：GAMEID/UMU_ID/WINEPREFIX/PROTONPATH/STEAM_COMPAT_*/SteamOS/NVAPI 等）由 VM 托管字典承载、**不序列化进编辑框**（此前全量显示被用户当成"启动命令列表"），持久层语义不变——保存时托管键 ∪ 用户键合并落盘（同名用户键优先），发行版下拉写托管 `PROTONPATH` 即时保存。回归 `LaunchSettingsTests`（ManagedKeys/PersistedGeneratedKeys/UserTypedKey 三钉）。
- **启动设置卡保存保留 `launch.umuId`**：SaveAsync 重建 LaunchOptions 时必须回填，否则 UMU_ID 退化为 umu-{gameId}（路由测试实锤）。
- 确认更新走**页内确认覆盖层**（GameSettingsPage 根 Grid 内：纱罩 `AppOverlayScrimBrush` + `launch-error-card` 卡；卡片 `MinWidth=340` 防版本号 token 被拆行；确认钮为 `danger-card` 红字红描边幽灵钮、文案"删除旧版并更新"——破坏性操作不与 accent 实心主钮共用样式；确认后 `UpdateProtonAsync` 装新版并清理同发行版旧目录）。回归 `DangerActionStyleTests`。
- 设置项实际变更落盘后弹轻提示：SaveAsync 对模板/工作目录/环境变量与旧值逐一快照对比，仅有变更才弹并列出变更字段（仅 `PROTONPATH` 变化即发行版切换，按"Proton 发行版"提示而非"环境变量"；无变更/校验失败不弹，失败仍走页内消息槽；服务器下拉切换也弹）。走 `GameItemViewModel.SettingsToastRequested` 事件转发，与状态 toast 同管线。
- **依赖卡（2026-09-27 增，管线见 docs/ARCHITECTURE.md §3.9）**：`x:Name="DependenciesCard"`，位于启动卡与游戏信息卡之间；可见性 = `DependencySectionViewModel.IsVisible`（Linux 且 DI 装配了安装器且目录非空，任一不满足整卡隐藏——Windows 上不存在该卡）。卡内：标题 + 说明 caption（限宽 640 Wrap）+ 依赖条目（`Ellipse.dep-dot` 8px 状态点：未装 = AppTextSecondary 灰、`.installed` = AppStatusOk 绿；名称 14px；状态文本 card-caption；右侧 `btn-sm` 安装/重装钮 `IsEnabled=CanInstall`）+ 整区不可用原因行（Direct/缺 wine/缺 Proton/prefix 未初始化四态）+ 进度行（ProgressBar 高 6 + 文本，`IsBusy` 驱动）+ **专属 save-msg 消息槽**（不与位置卡 Save/启动卡 UmuFeedback 混用；`UmuFeedbackHeadlessTests` 归属断言已升级为按文本归属，不再假设槽总数）。条目文案键 `deps_{id}_name/_desc`（如 `deps_cjk-fonts_name`）。回归：`DependencySectionViewModelTests`（VM 行为）、`DependencySectionHeadlessTests`（Linux 可见/Windows 隐藏 + 命令接线）。截图 `20/21-game-dependencies-*`。

## 5. 覆盖层与浮动层

- 启动失败覆盖层 = `LaunchErrorOverlay` 控件挂详情页 Panel 末尾（`GameItemViewModel.LaunchError` 驱动）。重试/关闭主次随 `CanRetry` 经 Classes 条件绑定对调：可重试时"重试"是 `launch-error-primary`、"关闭"退居 `launch-error-secondary`，不可重试时反转且重试隐藏（评审 P1-4）。回归 `DangerActionStyleTests`。
- **toast 门控**：启动失败覆盖层在场（CurrentPage 为游戏页且 HasLaunchError）时 `ShowToast` 直接丢弃新 toast——瞬态消息不得压过模态错误，覆盖层退场自动恢复（评审 P3-13）。回归 `ReviewPolishTests`。toast 卡用专属近实心底 `AppToastBackground`（亮暗成对；层次靠 1px 描边，不用 BoxShadow，原因见 skills `avalonia-ui`）。
- 启动遮蔽层 `BootSplash`：主窗口根 Panel 末位，`IsBooting` 驱动，默认 false 不影响测试/截图；生产路径经 `BeginBootSplash` 开启、`RunBootGateAsync` 放行（放行矩阵/时序见 docs/ARCHITECTURE.md §3.7，纯决策 `BootGate.ShouldRelease`）；代码后置自泵脉动条 + 0.25s 淡出，`SplashAnimationEnabled` 测试开关；Transform 不能 x:Name，脉动条位移运行时挂。

## 6. 关于页

- 信息卡带项目主页出口：`AboutViewModel.OpenProjectHomeCommand` → `IPlatformInfo.OpenInBrowser`（xdg-open/shell 关联），URL 常量 `AboutViewModel.ProjectHomeUrl`（评审 P3-15）。按钮为 GitHub 官方 mark 16px 幽灵图标钮（`ghost-home` 类复用 icon-btn 幽灵底：常态辅助色、hover 显微亮底转 accent；完整 URL 收进 ToolTip）——带底描边胶囊/整串 URL 文字两版均被用户否决（信息卡纯文本行内唯一带底控件即"突兀"，2026-09-25 用户定稿图标形态）。

## 7. 应用设置页（Controls/SettingsPage.axaml）

- 根 `Border.page` + `ScrollViewer` + `StackPanel MaxWidth=720 Spacing=16`，28px 页标题。六张 `card` 卡自上而下（2026-09-29 补记，结构快照）：
  ①**外观**：主题/语言两个 ComboBox 并排（`*,*` ColumnSpacing 24），`ThemeModes`/`Languages` 选项带 DisplayName 模板；hint caption。
  ②**应用背景**：只读 TextBox（`AppBackgroundPath`）+ 浏览/恢复两钮 + `AppBackgroundSave` 消息槽。
  ③**下载**：安装根目录行（`InstallRootBox` + inline 浏览钮，`*,Auto` ColumnSpacing 12——评审 P2-5 与游戏设置页位置卡同款）+ `InstallRootSave` 槽；限速行（TextBox 宽 140 + 保存钮 + `SpeedLimitSave` 槽）；自启 ToggleSwitch（On/Off 文案本地化）+ `AutostartSave` 槽。**四槽互不复用**（各自 SaveMessageSlot 实例）。
  ④**关闭按钮**（2026-09-29 增，0.1.3）：`x:Name="CloseActionCard"` 具名（截图测试滚动定位）——两个 RadioButton（退出应用 / 关闭窗口驻留托盘，GroupName=closeAction，`IsChecked` OneWay + 命令即时应用，同自启开关交互）+ hint caption（Wrap）+ `CloseActionSave` 槽。
  ⑤**网络（代理）**：`ProxyCard` 具名（截图测试滚动定位）——三个 RadioButton（跟随系统/直连/手动，GroupName=proxy）+ 地址 TextBox（`ProxyAddressBox`，缩进 24，`IsEnabled=IsProxyAddressEnabled`）+ 保存钮 + `ProxySave` 槽。`InstallRootBox`/`ProxyAddressBox` 具名供窗口级 Enter 处理器按名分发。
  ⑥**配置文件**：路径只读展示（13px 辅助色 Wrap）+ 打开所在目录钮。
  底部"返回游戏" `back-btn`。截图 `05-settings-dark`（滚动定位 ④卡）/`05b-settings-light`。

## 8. 唤取记录页（Controls/GachaPage.axaml）

- 根 `Border.page` + `ScrollViewer` + `StackPanel MaxWidth=760 Spacing=16`（2026-09-29 补记）。标题行：28px「唤取记录」+ 18px 游戏名（辅助色）。三块：
  ①**拉取与筛选**条（card，Padding 16,12）：`*,Auto,Auto` 三列——StatusText（13px 辅助色 Wrap）/ 池子 ComboBox（MinWidth 170）/ 刷新钮（`glass-onart pressable`，插画上按钮族复用）。
  ②**统计卡行**：`*,*,*,*` 四等分 card（Padding 16,8）——总计/五星/四星/距上次五星，数值 24px Bold；五星金 `AppGachaRare5`、四星紫 `AppGachaRare4`（对比度门禁管辖）。
  ③**记录列表**：ItemsControl（`gacha-rare5/4` Classes 着色稀有度列宽 28，名称中列，时间右列 12px 辅助色）。
  底部"返回游戏" `back-btn`（回详情页）。
