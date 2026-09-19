# 遗留工作清单（REMAINING）——审计后待办

- 建立时间：2026-09-19（双向审计 Phase 0-5 收尾时）；状态快照：commit 3593150
- **2026-09-19 Phase 4b-2**：P1 全部完成、P2 基本完成。
- **2026-09-19 Phase 4c**：P3/P4 全部完成（见"状态"列）。原审计清单实质清空，本文件转为
  "残余等价类论证 + 新增遗留"的收尾记录。
- 用途：新会话接手清单。接手前先读"新会话必读"一节（含 harness 教训，避免重踩）。

## 状态快照

- 行覆盖：4899/5756 = **85.11%**（2026-09-19 实测，Phase 4c 后；口径：仅本仓库 src/ 的 .cs；工具 `artifacts/audit/tools/`）
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
