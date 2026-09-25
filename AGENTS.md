# AGENTS.md

鸣潮 / 明日方舟：终末地 的跨平台游戏启动器。Avalonia 12 + .NET 10 + CommunityToolkit.Mvvm，Linux + Windows。

## 必读文档

改动前先读 `docs/`：**ARCHITECTURE.md**（分层/核心流程）、**DEVELOPMENT.md**（目录职责/TDD 工作流/覆盖率政策）、**GAME_CONFIG.md**（games.json 配置参考）。**UI 改动硬门槛（2026-09-25 用户指令，违反即返工）**：改任何 .axaml/视觉相关代码前必须先加载 `.zcode/skills/` 对应技能（avalonia-ui；涉及观感再加 desktop-ui-design）并读 docs/UI_STRUCTURE.md 对应节（结构/控件常数单一事实源），动手前先给出设计依据（引用规范条款说明"为什么这样设计"），交付前必跑 avalonia-ui-review 截图循环 + judge 审查——实锤：技能前置缺失的一轮三处设计被用户整体否决（"没有技能支撑默认是垃圾"），judge 循环在场的返工轮终态全部通过；**写/改无头测试前加载 avalonia-headless-testing**（Dispatch 三规则与并发约束在该技能）。CI 有两道质量守卫：行覆盖 ≥83% 门禁（scripts/coverage-gate.mjs）与 `Dispatch(async` 禁用形态 grep；另有每日定时变异冒烟批（.github/workflows/mutation-smoke.yml，变异批定义在 scripts/mutation-smoke.mjs，守卫测试空心化即红）。

## 常用命令

```bash
dotnet build -warnaserror            # 全解决方案构建；零警告是硬约束（TreatWarningsAsErrors=true）
dotnet format whitespace --verify-no-changes
# 测试（xunit.v3 + MTP；本环境 dotnet test 可能发现 0 个测试——直接跑测试产物更可靠；dotnet <dll> 为跨平台形态，.exe 仅 Windows）：
dotnet tests/YetAnotherGameLauncher.App.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.App.Tests.dll
# 单个测试（--filter-fqn 经 dotnet test 在本机实测零匹配，用 xunit 自带 runner 的 -method；注意 -method 对部分用例名会静默 Total:0——判定以全量运行 + -xml 为准，见 skills avalonia-headless-testing §4）：
dotnet tests/<测试工程>/bin/Debug/net10.0/<测试程序集>.dll -method "<完整类型名>.<方法名>"
```

## 文档同步（改代码时必须执行）

文档过期是本仓库的历史顽疾。**功能性改动落在下表左列时，必须在同一个变更内同步右列文档**；拿不准就全量跑一次 `/docs-sync` 技能（每周六 10:00 定时自动化也会跑）。

| 代码区域 | 必须同步的文档 |
|---|---|
| 视频播放器/无缝循环（FfmpegVideoBackdropPlayer、SeamAnalyzer、PrerollHandoff） | docs/ARCHITECTURE.md §3.7 |
| 设置模型与校验（AppSettings、GameCatalogService） | docs/GAME_CONFIG.md settings 表 + §5 校验规则 |
| 配置模板（samples/games.json） | docs/GAME_CONFIG.md + README 快速开始 |
| UI 结构/色值/控件常数（App.axaml、MainWindow.axaml） | docs/UI_STRUCTURE.md + skills avalonia-ui、desktop-ui-design |
| 新增/删除源文件 | docs/DEVELOPMENT.md §2 目录职责 |
| 新功能/测试数/常用命令 | README.md 与 README.en.md 的功能节与测试节（中英两份必须同一变更内同步改，README.en.md 是 README.md 的翻译、以中文版为准）、docs/DEVELOPMENT.md §2 |
| 测试基建与坑 | skills avalonia-headless-testing |

三条纪律：

1. **点值必须标注**：往文档写数量/色值/尺寸/清单这类快照时，要么带"实测日期"（如"2026-09 实测"），要么改成可推导命令；能不写快照就不写。
2. **单一事实源**：同一事实只在一处权威定义，其余位置链接过去（历史教训：README 与 GAME_CONFIG 各写一份且互相矛盾）。**指向规则/清单的概要也要粗**：次要位置只写节名指向，不复制逐条名称或要点清单——枚举即副本，本体演化必漂移（2026-09-24 实锤：「复审与修复纪律」第 7 条改为只扫不修后，DEVELOPMENT.md 指向段与记忆文件枚举各自漏改）。
3. **流程性描述跟代码走**：时序图、线程模型、抽象表这类描述行为的段落，改动对应代码时当作代码的一部分一起改，不留"以后再说"。

## 开发纪律（TDD，2026-09-22 起生效）

项目已从快速迭代转入 TDD 模式。**测试先行是硬约束**：src/ 生产代码的行为改动，先写测试并确认红（直跑测试 DLL 用 `-method` 过滤，红必须落在断言上），再写实现；修 bug 先写复现测试，禁止"修完补快照测试"。完整流程、合入标准（Definition of Done）与守卫定位见 docs/DEVELOPMENT.md §3——单一事实源，此处不复制。守卫红（覆盖率门禁/变异冒烟/`Dispatch(async` grep）= 流程失守信号，不是事后补测的许可。

## 复审与修复纪律（2026-09-24 五轮复盘沉淀，适用于所有会话含无人值守轮）

背景：一次用户可见 P0（背景视频全灭，5af2855）与随后两轮 review 共暴露 12+ 项问题，其中多项由**修复动作自身引入**（b1484b1..fefa12c）。本节是权威定义，DEVELOPMENT.md §3.6 承载同族的机器状态规则。逐条红线均有本仓库实锤锚点：

1. **绿 = 通过现有检查，≠ 正确**。守卫测试落地前做**变异自查**：故意破坏它守卫的东西，确认测试变红（M1 实锤：门控放行失效时资产代际测试空洞变绿；脚本化形式即 `scripts/mutation-smoke.mjs`，新守卫应纳入）。写测试警惕三种空洞：断言落在**死分支**上（对照生产实际产出值——如 `RegionForLanguage` 产 `cn`/`global` 而非 `zh`）；依赖的**基建/缝本身失效**（请求挂死≠守卫生效）；**夹具到不了被测路径**（假库字节永远测不到真绑定，成功路径零覆盖）。
2. **机器前提必须显式 Skip**。断言依赖"机器缺失 X"（系统库/工具/环境变量）的测试，必须在机器**有** X 时 `Assert.Skip`。注意注入缝只覆盖它显式注入的路径，**生产代码内部的回退/探测不受缝管**（R1 实锤：`systemLibraryProbe` 只门 EnsureReady 分支②，管不住 resolver 内部系统回退，装有精确 FFmpeg 9 的机器上假红；守卫先例 = JunkDownloadedDir 用例的 Skip 条件）。
3. **修复批次自带假设审计**。修 review 发现的批次本身就是新变更批次：提交前自查自己的新增面——每个新/改测试的机器前提、每条新注释的事实依据、每个新缝的语义。验证等级匹配变更等级：行为改动 = 红绿 + 真机（§3.6.1）；纯测试 = 双环境验证；假设 = 拿证据（M5 的 zip 实证做法）。递归无自然终点，每批收尾列出"本批引入的假设与未验证面"。
4. **语义变更必须对照上一版**。改缓存/契约/顺序类语义时 `git show <prev> -- <file>` 并排对照：①保留的注释是否仍讲真话（M3 实锤：负缓存行为已改、旧注释原样保留，矛盾自 b1484b1 存续至下一轮 review 才被发现）；②旧纪律是否被静默丢弃；③对旧结构机制的断言**先验证再写**进注释/测试注释（M3 红值预估 12 实测 1——对旧代码的机制模型本身是错的）。
5. **环境变体实验优于重读**。复查自己写的代码，重读会被自己的意图锚定；构造环境变体实跑一遍（`LD_LIBRARY_PATH` 指向伪造系统库、拔掉工具、加第二个并发调用方）能暴露读码读不出的机器/并发前提（R1 即如此发现，非读码所得）。
6. **机械操作按语义操作对待**。perl/sed/整段重写测试之后：重读关键 setup 行（重写时丢 `GateFirstRequest` 行实锤）；数据文件改动后 parse 校验（JSON 闭引号被 perl 吃掉实锤）；行号编辑优先改模式匹配（sed 误伤 375 行旧测试实锤）。本环境 shell 是 zsh：`set -- $var` 不做空格分词、双引号内 `~` 不展开（伪造符号链接实验连挂两次实锤）。
7. **无人值守轮只扫不修**（2026-09-24 用户决定）：仅做扫描/举证/立案，产出 = findings 清单（每条附证据锚点、影响面与建议修复思路，写入自主轮状态记录），**不改 src/、不提交修复、不做变异实验**——修复一律在用户在场的交互会话进行，天然带第二视角，并按本节与 §3.6 执行验证。轮记录仍须写明"待审面"（§3.6 规则 1 的真机验证记录由修复会话承担）。
8. **Review followup 批次与原始批次同标准**（2026-09-25 UI 轮四轮复盘沉淀）。修复 review 发现的每一条：①**行为类修复必须有可失败的复现测试或变异击杀**——"该场景原本没有测试"不是豁免理由（实锤：纱带探针采样点 x=680 修复因"长 chip 盖点"场景无测试，改完即绿等于没修，下轮 review 做长 chip 变异实验才实锤掩蔽面仍在）；②**注释/文档类修复必须同族扫描**——grep 被改事实的旧表述/旧值全仓清零后再提交（实锤：收起态指示点改 `CollapsedInsetX=0` 时只改了常量 XML doc，方法内注释、NavIndicator 元素注释、测试注释三处旧"点在块外/块外扩"故事残留到三轮 review 才清完）；③**采纳 review 结论 = 采纳待验证假设**（第 3 条假设审计在 followup 场景的特化）——review 给出的坐标/数值/机制断言必须换算坐标系、实测或实验验证后才能写进代码与注释（实锤：review 给出 page 局部坐标"最右 668"未换算窗口坐标直接采用，修复无效；重写注释时又按收起态侧栏 68 推域而测试实际跑展开态 264——引用的事故实测值 773 与自算域上限 736 矛盾就在眼前没察觉，引用实测值先与自己的推算对照自洽）。变异击杀实验由修复会话承担；无人值守轮按第 7 条只提出变异设计、不做实验。
9. **止损：review 循环必须终止，不得无限递归**（2026-09-25 用户最后通牒；本条收敛整个复审流程，优先级高于其余条款的"彻底性"诉求）。①**review 的退出判据 = 无 P0/P1 findings 且其余全部立案，不是 findings 清零**——P2/P3（文档漂移/注释过时/测试卫生/既有问题）记入记忆文件或自主轮状态走 backlog，禁止现场修复；②**修复批次只修 findings 点名的位置，禁止顺手改进**——同文件的重构/指针化/体例统一是独立变更须另立批次（实锤：x=680 探针修复顺手重写整段注释引入两个新坐标系错误；SKILL.md 收敛顺手改 §1 又多出两个 findings）；③**review 对象 = 本批 diff**——发现的既有问题只立案不修复；④**同一问题链的修复轮次上限 = 2**——第二轮修复后仍出 P0/P1 时回退整链交用户裁决，不启动第三轮；⑤规则文本（本文件/DEVELOPMENT.md/skills）随批次改动需用户点名或月度批量处理，不随单批 review 演进。

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
- 发布说明自 v0.1.1 之后的下一次发布起**中英双语**：CHANGELOG.md 新版本节内中文在前、英文对照在后（CI draft-release 从该节提取 GitHub Release 正文，随之中英双语）；v0.1.1 及之前保持仅中文、不追溯调整（2026-09-21 决定）。

## UI / 无头测试已知坑（实踩）

- **UI 结构/控件常数/指示点几何/各页布局**：单一事实源已迁 **docs/UI_STRUCTURE.md**（改 .axaml 前必读；窗口骨架/侧栏指示点/详情页/设置页/覆盖层/关于页）。
- **Avalonia 机制坑**（presenter 层前景/Transform x:Name/UniformToFill 裁切/IsHitTestVisible 剪枝/BoxShadow 真机灰板/编译绑定/ComboBox 引用匹配/Animation API 历史）：skills **avalonia-ui**「常见坑」「Linux 渲染」。
- **视觉判定/judge 仲裁/改机制先实验/截图清单（21 张归属）**：skills **avalonia-ui-review** §1/§3.5——交付前必跑该技能的截图循环。
- **Dispatch 三规则**：`Dispatch(Func<Task>)` 全形态禁用（发射后不管、吞断言，CI grep `Dispatch(async` 守卫）；`Dispatch(Action)` 同步 lambda 必须 await 且内部保持无 await；Bitmap/RenderTargetBitmap 只能在会话线程——细则 skills **avalonia-headless-testing** §4（哨兵 `DispatchSentinelTests` 常驻）。
- **headless 测试语义坑**（动画冻结首帧/窗口 Width 仅 Show 前接受且 MinWidth=920 钳制/Maximized 不铺满/像素探针全窗口帧坐标系/sequential 集合/-method 静默失灵/桩显式接口/VM 时序）：skills **avalonia-headless-testing** §4/§5——写/改无头测试前必加载该技能。
- **resolver 下载缝已可注入，真实 resolver 的测试必须离线构造**（2026-09-22 修复）：
  `FfmpegLibraryResolver(proxyManager, logger?, downloadClient?, downloadRoot?)` 两个可选缝——
  涉真实 resolver 的测试一律传 `new HttpClient(new StubHttpHandler())` + 临时目录根（先例
  `VideoBackdropPlayerCtsTests`/`FfmpegLibraryResolverDownloadTests`），否则无系统库/无缓存的
  新环境 `EnsureReady` 会真实下载约 60–70MB（GitHub API 2026-09-22 实测）并写真实用户数据目录
  （本机/CI 命中已下库属机器状态掩蔽）。**注意 DI 注册必须走显式工厂**：容器注册过 `HttpClient`
  （全局 30s）后类型激活会把 `downloadClient` 的 null 默认劫持为容器实例（15 分钟专用超时成
  死代码，组合根装配断言钉住）。
- **.NET 10 起 Dispose 后的 CTS `Cancel()` 是 no-op 不抛 ODE**（变异实验实锤：对已释放实例直接
  Cancel 的"崩溃"测试击不杀）——"已释放实例悬挂"不再表现为崩溃但仍是悬挂；防御性 try/catch
  的价值是语义显式化，别假设 ODE 会替你暴露 bug（先例：FfmpegVideoBackdropPlayer._cts 摘除）。
- **变异实验纪律**（scripts/mutation-smoke.mjs 头注释同款）：只对**已提交**状态做变异——`git checkout --` 还原会把目标文件上的未提交改动一并吃掉（实锤吃过一次修复）；变异后必须构建**测试工程**（测试 bin 持有独立依赖副本，只构建 src 项目不生效，Failed: 0 是假象）；测试数据必须能区分变异前后，否则"存活"无法判定。

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
- **tar 符号链接条目解压会被摊平成 0 字节普通文件**（2026-09-24 P0 实锤）：SharpCompress `WriteEntryTo` 对 SymbolicLink 条目写出空文件——BtbN FFmpeg 包里全部短名 soname（`libavcodec.so.63` 等）以链接形态存在，本机下载目录 14 个短名全 0 字节。`FfmpegLibraryResolver.ExtractArchive` 已改经 `IEntry.LinkTarget` 还原真链接（目标限同目录裸文件名，逃逸即拒）；凡解包不可信外部归档都要显式处理链接条目（与 zip `\` 条目名坑同族，见 `PackageInstallerService.ExtractArchive`）。
- **目录内原生库按依赖序预载，就绪探测必须覆盖实际用到的每个库**（2026-09-24 P0 实锤）：BtbN 库间 DT_NEEDED 只有同伴 soname 且**无 RUNPATH**，glibc 不会到被加载库自己的目录找依赖——不按 `LibraryDependencyOrder`（avutil 最先、avcodec 先于 avformat）先 dlopen 驻留，avformat/avcodec 必落 AutoGen throw-stub、起播时抛 `NotSupportedException`；而 `TryBind` 只探 `av_version_info()`（avutil 独立可加载 + ABI 稳定）会把这种残缺绑定误判为 READY。**教训泛化：探针函数必须覆盖真实调用面，"能解析一个符号"≠"绑定可用"；移除兜底前先证明主路径在真实环境工作（本条即 5af2855 移除系统符号链接回退引发背景视频全灭的根因，详见 DEVELOPMENT.md §3.6）**。
- **改原生库加载链（`FfmpegLibraryResolver`/FFmpeg 绑定/dlopen 相关）必须真机冒烟**：绑定成功路径无法离线覆盖（测试夹具是假库字节，真绑定每进程只有一次机会），全量套件绿在这里不构成证据——最低验证 = 跑应用看 `FFmpeg libraries ready` 日志 + 解码器协商 + 无 `Video backdrop playback failed`（2026-09-24 流程，DEVELOPMENT.md §3.6）。
- **IDE0005（未使用 using）有两类与肉眼相左的特例**（2026-09 实锤各一例）：仅提供扩展方法的 using 构建期**不报**但可能必需（`FfmpegLibraryResolver` 的 `SharpCompress.Common`——用户删后编译/测试/运行全过，说明那条其实可删；判据只有"删掉重编译"）；反之为必需 using 但肉眼看似未用（cref/扩展方法解析）。结论：删 using 以"删后编译"为准，IDE0005 的沉默不是充分证据。
- **背景视频播放器按游戏独占（transient，2026-09-21 起；原生库准备仍由 `FfmpegLibraryResolver` 单例共享）**：`Stop` 契约 = 取消循环 + 清空帧缓冲（`FfmpegVideoBackdropPlayer.StopCore`），配合 `PresentFrame` 代际门与 `GameItemViewModel` 的 `Frame 非空才点亮`——全停后迟到的旧帧通知以空帧缓冲为证不再点亮（回归：`VideoSource_FailedStart_DetachesFrameNotification` 等）。**切页一律暂停保活**：游戏页 ↔ 游戏页 / ↔ 非游戏页都走 `SuspendVideo`（`Pause` 泊车解码线程、帧/解码源/订阅保留，`HasBackgroundVideo` 不翻）；重进走续播快路径（会话存活直接 `Resume`，不起播不延迟）。全停只剩四处：保活淘汰（暂停队列超 2 路，`MainWindowViewModel.TrackParkedVideo` 淘汰最旧、重进自愈）、关窗/退出（`StopBackdropVideo` 逐游戏停）、列表重建（`RebuildGames` 停旧 VM）。`_activeVideoPage` 在切非游戏页后**仍指向被暂停的游戏页**（直到被另一游戏页替换）——置 null 会让暂停会话失去后续全停驱动。PlayAsync 后台任务收尾只在仍是本代时清会话标志（过期任务清零会让续播快路径误判失活走重启）。
- **SystemProcessRunner 即启即走 + 输出日志三坑**：① `BeginOutputReadLine` 事件会丢 stderr——`WaitForExit()` 只排空 stdout，必须自管 ReadLine 泵到 EOF；② `using var process` 在方法返回即 Dispose，会掐断管道，日志模式须泵收尾后再释放句柄；③ 进程可能在 `EnableRaisingEvents=true` 布防前退出，此时 Exited 永不触发，布防后要补查 `HasExited`（`SystemProcessRunner`）。
- 安装同步清理游离文件时 **prefix/compatdata 在保留名单**（`GameInstallService.PreservedEntries`）：Wine prefix 里有注册表/着色器缓存/用户数据，被清单外清理删掉等于毁掉游戏环境；Wine prefix 统一放 `~/.local/share/yagl/prefixes/<游戏id>`，绝不写进安装目录。
- **切 PATH 必须用 `Path.PathSeparator`，不能硬编码 `':'`**：Windows 上盘符 `C:` 会被切开，`SearchPath`/`FindOnPath` 返回缺盘符的相对根路径（CI Windows 腿红过）。同理，`GameLauncherService.ValidateCommand` 仅在 `pathValue is null && Windows` 时跳过裸命令预检——测试注入 `pathValue:""` 必须仍走 PATH 扫描，否则预检/错误覆盖层用例在 Windows 上会假绿成「已启动」。
- **测试里的路径断言两侧必须统一分隔符再比较**：期望值 `Path.Combine(...)` 在 Windows 产反斜杠、实际值常被归一成正斜杠，Linux CI 恰好两侧同斜杠掩盖问题（Windows CI 一次红 11 个）。两类实锤：①只归一实际值没归一期望值（`UmuComponentProvisionerTests`）；②期望值拼相对路径硬编码 `/` 而生产经 `GetFullPath` 产原生分隔符，或反之配置模板里的 `{installDir}/saves` 展开是**字面替换**不归一（`LaunchParameterRoundTripTests`）。比较前两侧都过 `Replace('\\', '/')`。
- **测试的平台分支本机只能执行到一边，另一边是死代码，本地全绿不代表分支正确**（2026-09-21 实锤：v0.1.1 发布被 windows 腿 5 个从未绿过的测试阻塞——均为 v0.1.0 后新增、本地 Linux 全绿的测试）。写 `OperatingSystem.IsWindows()/IsLinux()` 分支或 `Assert.Skip` 对侧断言时：①对照测试工厂的默认平台语义（`VmFactory` 默认 `FakePlatformInfo(isLinux:false)`=Windows 语义），分支需要的前置状态（如首运迁移成 native-umu 模板）必须在分支内自己驱动，不能假设对侧路径发生过（先例：`NativeUmuLaunchRoutingTests` Windows 分支曾因用默认平台导致门控不可达、启动假成功）；②真实平台上被前置门控挡住、不可达的场景（如 `NativeUmuLauncher.EnsureLinux` 先于准备器抛错挡住 ProtonDownloadFailed 重试分类），用 `Assert.Skip` 显式跳过并写明原因，不留假红；③Windows 上生产 `StreamWriter` 持写锁期间，测试轮询读同一日志必须 `FileShare.ReadWrite` 打开（`File.ReadAllTextAsync` 直接 IOException；Linux 允许并发读所以本地测不出，先例：`SystemProcessRunnerTests` 三兄弟）；④CI 失败注解只有用例名，断言消息需登录 Actions 看完整日志（logs API 要管理员权限，check-runs 注解公开可读）。
- **Windows 占用/只读语义是跨平台更新的头号杀手**（Linux `rename()`/`unlink()` 总能成功，问题只在 Windows 暴露）：被占用或只读的文件会让 `File.Move(overwrite:true)` 抛 IOException/UnauthorizedAccessException、让 `Directory.Delete(recursive:true)` 整体抛异常。统一防线（2026-09 全量修复）：原子写 `FileUtilities.WriteAtomicAsync`（解除只读+重试一次）、目录树 `FileUtilities.TryDeleteDirectory`（能删多少删多少）、下载落盘 `HttpFileDownloader.ReplaceDestination`（单独分类报"目标被占用"，绝不落进网络错误重试——重下多少遍都不会好）、背景落盘 `GameBackdropService` 换时间戳备用名。新增删除/覆盖代码先想这层。
- **`ZipFile.ExtractToDirectory` 在 Unix 把含 `\` 的 zip 条目名当字面文件名**（dotnet/runtime#98247，未修复）：Windows 打包器产出的包会在 Linux 解成安装根目录下的平铺垃圾文件，且"更新成功"。凡解包不可信外部 zip 必须手动遍历 `ZipArchive` 归一条目名（`PackageInstallerService.ExtractArchive`，顺带做 `..`/盘符穿越校验）。
- **Windows 的 `CreateProcess` 对裸命令名自动补 `.exe`，而 `File.Exists`/`IsExecutableFile` 不会**：预检 PATH 上的 "hpatchz" 时必须补试 `name + ".exe"`（`HpatchzApplier.ResolvePatchTool`），否则 Windows 误报工具缺失。Linux 还额外要求执行位（`FileUtilities.IsExecutableFile` 已含）。
- **`Process.StandardOutput.ReadToEnd()`（同步）会一直阻塞到子进程关闭 stdout，排在其后的 `WaitForExit(timeout)` 永远执行不到**——超时保护形同死代码，子进程挂起即卡死调用线程（实锤：`Program.cs` 启动期的 xrdb/hyprctl 查询曾会卡死 Main）。带超时的输出读取必须先 `ReadToEndAsync()` 再 `Wait(timeout)`，超时 Kill。
- **改带选项结构体的 API 必须逐字段核对默认值与旧行为的差异，并用全形态对照测试钉住**（2026-09-25 实锤，`FileUtilities.TryDeleteDirectory`）：为 F25 加 `IgnoreInaccessible` 时用了 `EnumerationOptions` 默认构造，其默认 `AttributesToSkip=Hidden|System` 在 Linux 上把点前缀条目（`.installed.ok`/`.yagl-*` 等，.NET 标记为 Hidden）从枚举剔除——含点文件的目录删不净、`Directory.Delete` 失败，UmuPrefix 清理路径全线回归（/tmp 探针：默认枚举 1 条 vs 完整 2 条），复审 R1 才抓到。修复=`AttributesToSkip = FileAttributes.None`（`FileUtilities.cs` 注释同款）。凡是"为加一个选项换了构造形态"的改动，同结构体其余字段逐一过一遍。
- **.NET 10 悬空符号链接三语义（2026-09-25 /tmp 探针实锤，Unix）+ 文件系统防护必须枚举目标全部既有形态**：①`File.Exists(悬空链接)=true`（跟随语义失灵——UmuPrefix 曾因此漏判悬空 pfx 链接致启动永久失败，F21）；②`File.GetAttributes(悬空)` 不抛、返回链接自身属性（`ReparsePoint=true`）；③`Directory.Delete(悬空)` 抛 `DirectoryNotFoundException`（删链接须用 `File.Delete`）。**Windows 经典语义不同：`File.Exists(悬空)=false`**——放在 `File.Exists` 门内的链接检查会被 Windows 悬空形态绕过（`PackageInstallerService.ExtractArchive` 的目标 reparse 检查因此必须无条件执行，复审 R2/R8）。写解压沙箱/覆盖删除类防护时，目标位置的形态全集 = 不存在/真实文件/真实目录/工作链接/悬空链接，逐一建模；`FileUtilities.IsReparsePoint` 的 catch 靠 IOException 臂即可覆盖 FNFE/DNFE（二者均为其子类，官方继承链探针复核——曾误加冗余 FNFE 臂并写入错误注释，复审三连修正）。已知残留面（立案 bugs.md）：**硬链接**无 ReparsePoint 标记、.NET 无可移植 link count API，`ExtractToFile(overwrite)` 经预埋硬链接可写穿同卷外部文件。
- **写 catch 前查官方异常表全列；加 catch 后必须推演"接住之后呢"**（2026-09-25 实锤）：①`Process.Kill(entireProcessTree:true)` 官方抛 Win32Exception（无法终止/正在终止）+ AggregateException（子树未全终止），不止 InvalidOperationException——F22-2 曾只补 Win32Exception 漏 AggregateException；②`FileInfo.Length` 对文件/父目录缺失抛 FNFE，`File.OpenRead` 对父目录缺失抛 DNFE——同族 TOCTOU 不同异常类，catch 收窄即漏（F18 复审 R3）；③F22-2 补 catch 后引入"Kill 真失败（受保护进程拒绝终止）→ `WaitForExitAsync(None)` 永久挂"新风险——**接住异常不是终点**，接住后的下游路径（挂死/泄漏/分类丢失）要重新推演，等待类调用一律有界。
- **GitHub release 资产顺序 = 上传顺序，与架构无关**：GE-Proton11-6 曾把 aarch64 资产排在 x86_64 之前，"取第一个匹配的 tar"在 x86_64 主机上装出 ARM Proton（目录名还剥掉架构后缀看不出异常，其 toolmanifest 又会把 arm64 Steam Runtime 连带拖下来）。防御在 `UmuComponentProvisioner`：`SelectTarAsset` 按主机架构过滤资产名后缀（同架构优先、无后缀次之、反向排除，构造参数 `hostArchitecture` 可注入测试），解压后再读 `files/bin/wineserver` 的 ELF e_machine 兜底（0x3E=x86-64、0xB7=aarch64），不符即删目录报错；**本地解析同样过滤**（`FindInstalledProton` byName 与 `FindLatestLocalProton`），已装的错架构目录视同缺失、下次启动自动重装自愈（`MatchesHostArch`）。更新清理（`PruneOtherProtonVersions`）与安装共用 proton.lock；确认覆盖层提示先退出运行中的游戏（删旧版会让运行中游戏的延迟加载失效）。
- **协议/格式的行为结论只认一手源码，二手摘要必须标注未核实**（2026-09-25 实锤，F24 两连错）：WebSearch 摘要称 Valve KeyValues 转义表为 `\n/\t/\\/\"`——据此写的注释与测试断言全是错的；拉 `tier1/utlbuffer.cpp` raw 源码实证真实表为 `\n \t \v \b \r \f \a \\ \? \' \"`（未知转义=NUL+保留后续字符，与"丢反斜杠"不同）。**同一错误在复审中差点二连**：凭记忆怀疑 `\r` 不该转义，查原文才发现 `\r` 恰在表内。解析器/协议逆向类结论：摘要→源码溯源是硬门槛；注释里引用外部行为必须写明出处层级（"源码实证"vs"摘要未核"）。
