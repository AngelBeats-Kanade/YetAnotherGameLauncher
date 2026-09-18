# Phase 4b 变异抽查击杀表（2026-09-19）

## 方法

手工定向变异：每个变异 = 单点语义破坏（运算符翻转/比较目标替换/守卫摘除）→ 仅构建受影响测试工程 →
定向运行守卫该行为的测试 → **必须变红（击杀）** → git 还原。全套件与覆盖率在全部还原后复测。

教训记录（harness）：变异必须构建**测试工程**（测试 bin 持有自己的依赖副本，只构建源项目不生效）——
第一轮 M2/M3/M4 的 Failed: 0 系此因，非测试存活。

## 击杀表

| # | 变异点 | 变异内容 | 击杀测试 | 结果 |
|---|---|---|---|---|
| M1 | LocalStateService.Load :29 | `GameId == gameId` → `!=` | LocalStateServiceTests.SaveThenLoad_RoundTrips | ✅ 击杀 |
| M2 | GameLauncherService.Expand :187 | `{installDir}` 替换目标改写（占位符失效） | LaunchParameterRoundTripTests.SavedLaunchSettings_DriveBuildPlanExactly | ✅ 击杀 |
| M3 | HttpFileDownloader.Verify :185 | MD5 不等比较取反 | HttpFileDownloaderTests.Md5Mismatch_RetriesFromScratchThenThrows | ✅ 击杀 |
| M4 | GameBackdropService.IsCacheFreshFor :102 | 版本门控误比 Region（恒失效） | GameBackdropServiceTests.Resolve_VersionUnchanged_SkipsResolverAndNetwork | ✅ 击杀 |
| M5 | UpdatePlanner.Plan :21 | 差分源版本 Contains 取反 | UpdatePlannerTests.Plan_LocalVersionMatchesPatch_Incremental | ✅ 击杀 |
| M6 | SpeedLimiter.Acquire :51 | 排队窗口抹零（限速失效） | SpeedLimiterTests.Acquire_QueuesBeyondBudget | ✅ 击杀 |
| M7 | GameItemViewModel.StartVideoAsync | 新增 catch 类型错配（InvalidOperationException 逃逸） | VideoSource_PlayAsyncThrows_FallsBackToPosterWithoutCrash | ⚠️ 存活（见下） |
| M8 | GameItemViewModel.SelectServerOptions :592 | 摘除 B 服排除 | GameItemErrorPathTests.BackdropRequest_GlobalRegion_SkipsBilibiliServer | ✅ 击杀（测试数据修正后：B 服须排首位） |

**击杀率 7/8**。

## M7 存活论证（等价防御，记录在案）

StartVideoAsync 的新增 catch 与外层 LoadAssetsCoreAsync 的 `catch (Exception)`（装饰性资源静默回退）
构成双层防御：变异后 InvalidOperationException 被外层捕获，可观察副作用（HasBackgroundVideo/Frame/
PlayedPaths）与原行为一致，测试无法区分。残余风险：`SetDetailActive → _ = StartVideoAsync`（:483）
路径不在 LoadAssetsCoreAsync try 块内，异常经未观察任务静默——但下游 `OnVideoFrameUpdated` 以
"帧缓冲非空"为准不会点亮，实际危害为订阅泄漏（Frame 为 null 时无行为差异）。处置：保留新 catch
（纵深防御 + 明确注释）；SetDetailActive 路径的订阅泄漏列 Phase 5 遗留清单（真机行为验证）。

## M8 教训

初始测试数据（cn 在前）与变异等价——`FirstOrDefault()` 无排除时仍选中 cn。**测试数据必须让
被排除项排在最前**才能赋予测试击杀力。已修正配置顺序并在原始代码上复验绿。

## 结论

四条主链路（启动参数展开/下载校验/更新规划/背景门控/限速/状态读取）的守卫测试具备真实击杀力，
Phase 1 审计"✅"判定的核心测试不是空心的。全量变异扫描（数千变异）超出本阶段机器时间预算，
按批准计划以抽查批次落地；CI 每日变异冒烟批列入 Phase 5 遗留清单。
