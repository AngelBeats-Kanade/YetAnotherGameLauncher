# AGENTS.md

鸣潮 / 明日方舟：终末地 的跨平台游戏启动器。Avalonia 12 + .NET 10 + CommunityToolkit.Mvvm，Linux + Windows。

## 必读文档

改动前先读 `docs/`：**ARCHITECTURE.md**（分层/核心流程/平台后端）、**DEVELOPMENT.md**（目录职责/TDD 工作流/覆盖率政策）、**GAME_CONFIG.md**（games.json 配置参考）、**UI_STRUCTURE.md**（UI 结构/控件常数，改 .axaml 必读）、**PITFALLS.md**（跨平台/.NET 踩坑手册）。**UI 改动硬门槛（2026-09-25 用户指令，违反即返工）**：改任何 .axaml/视觉相关代码前必须先加载 `.zcode/skills/` 对应技能（avalonia-ui；涉及观感再加 desktop-ui-design）并读 docs/UI_STRUCTURE.md 对应节（结构/控件常数单一事实源），动手前先给出设计依据（引用规范条款说明"为什么这样设计"），交付前必跑 avalonia-ui-review 截图循环 + judge 审查——实锤：技能前置缺失的一轮三处设计被用户整体否决（"没有技能支撑默认是垃圾"），judge 循环在场的返工轮终态全部通过；**写/改无头测试前加载 avalonia-headless-testing**（Dispatch 三规则与并发约束在该技能）。CI 有两道质量守卫：行覆盖 ≥83% 门禁（scripts/coverage-gate.mjs）与 `Dispatch(async` 禁用形态 grep；另有每日定时变异冒烟批（.github/workflows/mutation-smoke.yml，变异批定义在 scripts/mutation-smoke.mjs，守卫测试空心化即红）。

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
| 跨平台文件/进程/.NET 通用坑（FileUtilities、HttpFileDownloader、SystemProcessRunner 等） | docs/PITFALLS.md |
| 平台后端/窗口状态（WaylandBackendPolicy、WindowStateMapper、Program.cs） | docs/ARCHITECTURE.md §3.8 |
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

## 坑索引（一行触发器，细则在落点）

- **UI 结构/控件常数/指示点几何/各页布局**：单一事实源已迁 **docs/UI_STRUCTURE.md**（改 .axaml 前必读；窗口骨架/侧栏指示点/详情页/设置页/覆盖层/关于页）。
- **Avalonia 机制坑**（presenter 层前景/Transform x:Name/UniformToFill 裁切/IsHitTestVisible 剪枝/BoxShadow 真机灰板/编译绑定/ComboBox 引用匹配/Animation API 历史）：skills **avalonia-ui**「常见坑」「Linux 渲染」。
- **视觉判定/judge 仲裁/改机制先实验/截图清单（21 张归属）**：skills **avalonia-ui-review** §1/§3.5——交付前必跑该技能的截图循环。
- **Dispatch 三规则**：`Dispatch(Func<Task>)` 全形态禁用（发射后不管、吞断言，CI grep `Dispatch(async` 守卫）；`Dispatch(Action)` 同步 lambda 必须 await 且内部保持无 await；Bitmap/RenderTargetBitmap 只能在会话线程——细则 skills **avalonia-headless-testing** §4（哨兵 `DispatchSentinelTests` 常驻）。
- **headless 测试语义坑**（动画冻结首帧/窗口 Width 仅 Show 前接受且 MinWidth=920 钳制/Maximized 不铺满/像素探针全窗口帧坐标系/sequential 集合/-method 静默失灵/桩显式接口/VM 时序）：skills **avalonia-headless-testing** §4/§5——写/改无头测试前必加载该技能。
- **跨平台文件/进程/.NET 语义坑**（悬空符号链接/Windows 占用只读/路径分隔符/选项结构体默认值/catch 异常表/SystemProcessRunner/CTS）：docs/**PITFALLS.md**。
- **FFmpeg 原生库供给（tar 链接还原/依赖序预载/测试缝与 DI）/播放器契约/平台后端与窗口状态**：docs/**ARCHITECTURE.md** §3.7/§3.8。
- **变异实验纪律**（已提交状态/构建测试工程/数据区分变异前后）：docs/**DEVELOPMENT.md** §3.7。
- 应用运行中会锁住 `bin` 下的 DLL（构建报 MSB3021/3027）——先 `taskkill //F //IM YetAnotherGameLauncher.exe` 再构建；构建输出用 `tail` 截断会漏看这类错误。
- 本机 Git Bash 的 `grep` 实为 **ugrep**：从仓库根递归扫描可能静默失败（报错被 `2>/dev/null` 吞掉后表现为"零匹配"）——查 tracked 内容用 `git grep`，全盘扫描用 `find ... | xargs` 交叉验证。
