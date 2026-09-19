# 遗留工作清单（REMAINING）——审计后待办

- 建立时间：2026-09-19（双向审计 Phase 0-5 收尾时）；状态快照：commit 3593150
- **2026-09-19 Phase 4b-2**：P1 全部完成、P2 基本完成。
- **2026-09-19 Phase 4c**：P3/P4 全部完成（见"状态"列）。原审计清单实质清空，本文件转为
  "残余等价类论证 + 新增遗留"的收尾记录。
- **2026-09-20 全盘复审**：新发现 P1×1、P2×3、P3×约10、P4×约12（修复清单见文末"2026-09-20 全盘复审"节，
  含与上轮审计记录的逐项对照与根因归纳）。
- 用途：新会话接手清单。接手前先读"新会话必读"一节（含 harness 教训，避免重踩）。

## 状态快照

- 行覆盖：4906/5758 = **85.20%**（2026-09-19 实测，review 修复后；口径：仅本仓库 src/ 的 .cs；工具 `artifacts/audit/tools/`）
- 测试：**668** 个（Core 274 / Kuro 58 / Hypergryph 17 / App 319），本地两轮全绿
  （其中 4 个 Windows 腿用例在 Linux 上显式 Skip，命中靠 windows-latest runner）；零警告构建；format 干净
- CI 守卫：行覆盖 ≥83% 门禁 + `Dispatch(async` 禁用形态 grep + **每日变异冒烟批**（mutation-smoke.yml）
- 证据链：docs/audit/（REPORT.md / tests/ 527 判定 / business/ 91 文件 / MUTATION.md 含 M1-M11）

## 审计遗留清单（全部完成，留档）

| 优先级 | 事项 | 结果 | 详细记录 |
|---|---|---|---|
| P1 | FakeChannel 失败注入 | ✅ 4 个可注入异常属性（2026-09-19 4b-2） | 本文件历史 |
| P1 | GameItemViewModel 离线兜底 + 进度卡生命周期 | ✅ ×7 用例，M9/M10 击杀（4b-2） | business/App/ViewModels.md |
| P2 | LaunchSettingsViewModel env 边界 + 重入守卫 | ✅ internal 直测 ×10 + GatedProvisioner 重入（4b-2）；4c 再补确认更新失败/取消 ×2 | business/App/ViewModels.md |
| P2 | SystemProcessRunner env/释放/瞬秒分支 | ✅ ×5 用例（4b-2） | business/Core/SystemProcessRunner.md |
| P2 | KuroGachaService 占用/畸形 URL/缓存容错 | ✅ ×8 用例（4b-2） | business/Channels.md |
| P3 | Windows CI 腿 | ✅ 4c：IsExecutableFile Windows 分支、AppPaths %LOCALAPPDATA%、HpatchzApplier `.exe` PATH 补试、**740 提权回退整段**（现场编译 requireAdministrator 桩 exe；已提权环境显式 Skip）——4 个用例 Linux 腿显式 Skip，**首次命中验证待 push 后看 windows-latest 腿** | business/Core/SystemProcessRunner.md |
| P3 | 每日变异冒烟批 | ✅ 4c：scripts/mutation-smoke.mjs（stale 规格检测/构建测试工程/自动还原）+ mutation-smoke.yml（每日 UTC 03:17 + 手动触发）。本地首批 5/5 击杀 | business/MUTATION.md |
| P3 | SetDetailActive 订阅泄漏（M7 残余） | ✅ 4c：订阅/退订收拢 StartVideoAsync finally（结构性消除）；泄漏探针 VideoSource_FailedStart_DetachesFrameNotification（注入非空帧 + 手动通知才可见）经 M11 击杀验证 | business/MUTATION.md M7/M11 |
| P3 | 杂散容错行 | ✅ 4c：背景 meta 损坏、暂存清单损坏、LocalizationService 缺资源（en-GB 前缀放行回退）、确认更新失败/取消、Patching/CleaningUp/Verifying 阶段文案臂、FormatBytes KB/MB/GB 臂、SeamAnalyzer 空跨度 + 指针重载（App.Tests 开 AllowUnsafeBlocks）、DisplayName 前缀回退 | 各 business/ 记录 |
| P4 | 排除文件可回收 | ✅ 4c：FfmpegLibraryResolver.LocateLibraryDir 直测 ×4（本来已 internal）；hyprctl 缩放解析提取为 **CompositorScaleParser**（非排除新文件，可测且计入覆盖）——顺带修掉原实现空数组返回 0 而非 null 的边角缺陷 | business/App/Services-Views-Excluded.md |

## 残余未覆盖行的定性（等价类论证，接受保留）

- **竞态容错类**（SystemProcessRunner :203-209/:259-271/:292-295/:312-314、GameBackdropService WriteMeta IOException 等）：
  防御代码删除后仅在进程竞态/IO 竞态窗口可见，等价类豁免（MUTATION.md"处置汇总"）。
- **构造困难类**：KuroGachaService prefix 枚举守卫（:117-138，需构造枚举中途 IOException）、
  LocalizationService 内嵌资源 JsonException（:82-84，构建物损坏场景，无注入缝隙）、
  SystemProcessRunner 740 段的个别容错行（双层释放竞态）。
- **展示边角**：GameItemViewModel 服务器计数/渠道显示/抽卡入口的残余展示分支（~15 行，行为即字面映射）。
- Windows 腿新增用例的首次 runner 命中验证（推上去看一眼 mutation-smoke 与 windows 腿日志即可）。

## 新会话必读（harness 教训，重踩成本高）

1. **测试判定口径**：docs/audit/tests/CRITERIA.md——所有测试默认可疑，双向可证伪才算合格。
2. **Dispatch 三规则**（AGENTS.md"UI / 无头测试已知坑"节）：`Dispatch(async)` 禁用（吞断言）；
   `Dispatch(Action)` 必须 await；Bitmap 只能会话线程。先例：BackgroundImageServiceTests / SettingsHeadlessTests。
3. **变异验证**：必须构建**测试工程**（测试 bin 持有独立依赖副本）——4b-2/4c 各实踩一次；
   测试数据必须能区分变异前后（M8 教训）。冒烟批已脚本化：`node scripts/mutation-smoke.mjs [id...]`。
4. **本机跑测试**：`dotnet test` 可能 0 发现——直跑 `dotnet tests/<工程>/bin/Debug/net10.0/<程序集>.dll`；
   单测试用 xunit runner 的 `-method`（FQN 注意命名空间：App.Tests 的无头窗口测试在 `UiTests`，
   其余在 `AppTests`）。
5. **应用运行中锁 bin**（MSB3021/3027）：构建前 `taskkill //F //IM YetAnotherGameLauncher.exe`（Windows）。
6. **每轮改动后**：全量 4 套件 ×2 轮 + `dotnet format whitespace --verify-no-changes` + 覆盖率复测。
7. **Progress&lt;T&gt; 回调异步到达**：断言进度字段须有界轮询或订阅 PropertyChanged 历史
   （先例：GameItemProgressTests；M10 证明轮询必要）。多文件批量下载的进度是**累计字节**，
   KB/MB 窗口会被跳过——测 FormatBytes 文案臂用单文件清单逐轮驱动（先例：Install_ProgressFormats）。
8. **VM 测试里 `Games` 集合在 InitializeAsync 之后才有值**（先例踩坑：访问 `Games[0]` 前必须先初始化）。
9. **变异实验只对已提交状态做**（2026-09-19 review 实锤）：冒烟批/手工变异的 `git checkout --`
   还原会把目标文件上的未提交改动一并丢弃（实际吃掉过 StartVideoAsync 的 finally 修复并造成
   文档与代码短暂失配）；冒烟脚本已加脏树守卫，手工变异同理先提交。

## 2026-09-20 全盘复审与上轮审计对照

三路并行复审（文档漂移 / Core+Channels / App+测试+CI）的新发现，逐项对照上轮 `docs/audit/` 记录，
并归纳"已覆盖代码无实锤 bug"结论与 30 项新发现并存的方法论根因。

### 对照结论：上轮"看过且有记录，但定性差一步"（6 项）

| 本轮发现 | 上轮记录 | 差的一步 |
|---|---|---|
| 进程超时被当"用户取消"静默吞 | SystemProcessRunner.md:14 记录了"rethrow 被 timeoutCts 语义合并"仍判 ✅；REPORT.md:34 还给 App 层 catch 补 OCE | Timeout 测试断言"OCE 传播了"，没断言"消费端可区分"；两处各自正确的局部修改合成了全局盲区 |
| 代理 Manual 非法地址回退 System | Services-Batch2.md:18："三态互斥+非法地址回退 System，四用例全测" | **有测试钉住的既定设计，非漏检**——本轮复核后维持不改，仅在此登记（UI 校验拦住正常路径，仅手改配置可触发） |
| 切 Proton 发行版吞掉输入中的"无=行" | ViewModels.md:38 列了"无=行"为待补边界 | 归类为"校验分支补测"，未做"即时保存重写文本框"的交互场景推演 |
| 服务器切换竞态 + async void 逃逸 | ViewModels.md:26 把 586-617/691-717（正含两个 async void 与服务器切换刷新）列为补测缺口 | 区段被看见：并发看到 `_refreshTask` 去重即当守卫齐备；async void 只查处理器首层调用（LaunchAsync 全捕获），没追 catch 体内 CreateLaunchError 自身可抛 |
| 容量淘汰的 toast 不停计时器 | ViewModels.md:48 记录"自动销毁定时器 4 行未覆盖" | 记录的是定时器行为测试缺口，不是淘汰路径的资源卫生 |
| 抽卡同秒同名合并/游标/qualityLevel | Channels.md:10 记录了容错分支清单 | 丢记录后果未定性；游标问题需真机抓包，上轮无 live 断言手段 |

### 对照结论：上轮"零记录"——方法论边界（重点 6 项）

| 本轮发现 | 为什么上轮方法看不见 |
|---|---|
| **组合根漏传 proxyManager（P1，直连/自定义代理整体失效）** | App.axaml.cs 整文件豁免（Services-Views-Excluded.md:20），豁免理由"类型正确性由消费方全量测试背书"对**可选参数**失效（默认 null 编译期不可见）；消费方测试 VmFactory 镜像了同一遗漏；代理测试只断言 games.json 持久化。覆盖率门禁按设计照不到豁免文件 |
| **鸣潮预下载窗口期增量必失败（P2，跨块 patchConfig）** | 测试通过是因为 fixture 恰好让 live 块满足查询（查 3.5.0→3.6.0 而 fixture 的 3.6.0 在 predownload 块）——"fixture 即契约"的循环论证；旁证：ChannelVersionInfo.cs:12 注释承诺 predownloadSwitch 而 KuroModels 从未建模该字段，注释与 DTO 脱节在"以测试为准"的判定中无处落脚 |
| **Verify 把空串/0 当校验目标（服务端字段缺失→数十 GB 重下 3 遍）** | Verify 单元判 ✅（双分支已测），但 ""/0 的上游注入在另一批次的文件（Gryphline TryParse 失败→0、Kuro pack.Md5 可空串）——逐文件批次切分无跨层数据流追踪 |
| **Windows 自启动 reg 退出码不检查** | 测试断言"命令形状"（FakeProcessRunner 恒成功）；Enable/Disable 不查退出码而 IsEnabledAsync 查的不一致，在形状断言颗粒度下不可见 |
| **更新成功后 .yagl-bak 删除失败推翻已完成结果** | 回滚分支逐行清点到位，但尾段删除循环在 try/catch 之外且被成功路径测试行覆盖——需"File.Delete 可被 Windows 锁打崩 + 位置在 catch 外 + 成功已达成"三跳推理 |
| **死代码/孤儿键/死包/死条件/tooltip 未接线/语言切换快照/Format 文化/文档漂移** | 上轮目标函数是"测试有效性+覆盖率+正确性"：行覆盖看不见无引用；.axaml 只做编译绑定/主题成对/截图三查；MainWindow.axaml.cs 只有覆盖率统计无逐行语义表（Services-Views-Excluded.md:26）；i18n 完备性与文档时效性是另一学科（docs-sync）。**零记录=范围外，不是执行疏漏** |

### 根因归纳（为何上轮结论与本轮发现并存）

REPORT.md:57 的诚实边界（"83.49% 行覆盖+变异抽查 ≠ 无 bug 证明"）即答案，本轮发现全部落在其自认能力圈外：

- **R1 测试↔代码互证闭环**：测试错了/缺了判定跟着错（fixture 即契约、VmFactory 镜像、断言方向）——对"代码 vs 现实世界"（协议、组合根、消费端语义）结构性失明；
- **R2 逐文件批次切分**：跨文件数据流与异常生命周期无主（Verify 空值、超时 OCE 吞点、先改写后保存）；
- **R3 竞态按"覆盖缺口/容错等价类"归类**：不做对抗性交错分析（服务器切换竞态）；
- **R4 排除文件与视图层浅覆盖**：App.axaml.cs / MainWindow.axaml.cs / .axaml 三处豁免或降级各漏一类；
- **R5 审计目标函数不含卫生类**：死代码/孤儿键/死包/i18n/文档时效不在测量范围；
- **R6 看过但定性差一步**：超时语义、async void 首层逃逸、无=行、代理回退。

后续全量审计若再做，应在 Phase 3 增加"跨层数据流与异常生命周期"专项、在 Phase 0 把组合根装配纳入
行为级断言（可注入的 fake 组合根），并单列卫生类（死代码/资源/i18n/文档时效）清点。

### 本轮修复清单（2026-09-20，详见同日提交）

- 测试基线：**686** 个（Core 284 / Kuro 61 / Hypergryph 17 / App 324，2026-09-20 实测，
  较上轮 +18：代理装配 ×3、预下载跨块 ×1、修复对齐守卫 ×2、刷新竞态 ×1、
  校验语义 ×2、超时/取消 ×1、自启动退出码 ×1、备份删除 ×1、取消检查点 ×1、空版本 ×1、
  predownloadSwitch 关闭分支 ×2、启动日志唯一性 ×1、env 半行 ×1——按套件执行数口径）。

| 级别 | 事项 | 处置 |
|---|---|---|
| P1 | 组合根漏传 proxyManager | ✅ 补传 + VmFactory 传真对象 + 行为级测试（handler 状态断言） |
| P2 | Kuro 预下载增量跨块查找 | ✅ live 优先回退 predownload 块 + fixture 回归 |
| P2 | 修复对齐按服务器 latest 清单可能改回旧内容 | ✅ 仅 latest==staged.Version 才修复 |
| P2 | 切服务器竞态旧结果覆盖新状态 | ✅ RefreshAsync 代际门 |
| P3 | Verify 空串/0 语义、超时可区分、自启动退出码、.yagl-bak 尾段、暂存落位分类/取消检查点、空版本不报更新、predownloadSwitch 兑现、日志名防同秒覆盖 | ✅ 各配回归测试 |
| P3 | async void 兜底、SaveAsync 时序、Gacha catch 宽化、LaunchSettings 订阅泄漏、语言切换快照重建、finally OCE、Format 文化 | ✅ 各配回归测试 |
| P4 | 死条件、window_restore 接线、toast 淘汰停表、env 半行保留、死代码/孤儿键/死包清理、失实注释 | ✅ |
| 登记 | 代理 Manual 非法回退 System（既定设计）、磁盘满误分类网络重试、抽卡游标需真机抓包、切语言视频停-重播、同秒同名抽卡合并 | 留档不改 |
