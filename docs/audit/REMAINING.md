# 遗留工作清单（REMAINING）——审计后待办

- 建立时间：2026-09-19（双向审计 Phase 0-5 收尾时）；状态快照：commit 3593150
- **2026-09-19 Phase 4b-2 更新**：P1 全部完成、P2 基本完成（见下表"状态"列）；快照同步刷新。
- 用途：新会话/后续迭代的接手清单。**只列已归因、有方案的事项**——每项标注记录位置，
  接手前先读"新会话必读"一节（含 harness 教训，避免重踩）。

## 状态快照

- 行覆盖：4870/5749 = **84.71%**（2026-09-19 实测，Phase 4b-2 后 +70 行；口径：仅本仓库 src/ 的 .cs；工具 `artifacts/audit/tools/`）
- 测试：641 执行全绿（Core 269 / Kuro 57 / Hypergryph 17 / App 298）；零警告构建；format 干净
- CI 守卫：行覆盖 ≥83% 门禁（scripts/coverage-gate.mjs）+ `Dispatch(async` 禁用形态 grep
- 证据链：docs/audit/（REPORT.md 总报告 / tests/ 527 判定 / business/ 91 文件逐行审计 / MUTATION.md 击杀表，含 M9/M10 增补抽查）

## 遗留清单（按优先级）

| 优先级 | 事项 | 位置/行数 | 方案 | 详细记录 | 状态 |
|---|---|---|---|---|---|
| P1 | GameItemViewModel 残余分支：进度卡生命周期（RunUpdateAsync 进度消费/完成清卡）、离线资产兜底分支（RefreshAsync catch 内 :266-284） | GameItemViewModel 未覆盖 ~90 行 → **48 行** | 进度矩阵用例 + FakeChannel 失败注入 | business/App/ViewModels.md | ✅ **2026-09-19 完成**（GameItemOfflineTests ×4 + GameItemProgressTests ×2 + StartupAssetPreloadTests 离线兜底 ×1；变异抽检 M9/M10 击杀） |
| P1 | TestSupport 增强：FakeChannel 失败注入（FetchVersionException 之类） | tests/YetAnotherGameLauncher.TestSupport/FakeChannel.cs | 加可注入异常属性 | 本文件新增 | ✅ **2026-09-19 完成**（`VersionInfoError`/`ManifestError`/`IncrementalManifestError`/`PredownloadManifestError` 四属性，后续容错用例直接复用） |
| P2 | LaunchSettingsViewModel 残余：保存 env 解析边界（空值/重复键/无=行）、检查更新重入守卫 | LaunchSettingsVM 未覆盖 ~70 行 → **55 行** | 边界用例组 + 重入守卫 | business/App/ViewModels.md | ✅ **2026-09-19 完成**（解析函数转 internal 直测 ×10；GatedProvisioner 重入用例 ×1。残余：确认更新失败/取消分支 :392-404，见 P3 杂散行） |
| P2 | SystemProcessRunner：env+日志组合（:44-50/:68-73）、启动失败释放（:136）、瞬秒进程 HasExited 分支（:156-159/:172-174） | ~10 行 → 竞态容错行外全覆盖 | 三个小用例；竞态容错行按等价类论证 | business/Core/SystemProcessRunner.md | ✅ **2026-09-19 完成**（×5 用例；156-159 Exited 分支由长存活进程用例确定性命中；172-174 与 203-209/259-271/292-295/312-314 属竞态容错等价类，逐行论证保留） |
| P2 | KuroGachaService 容错：日志被占用跳过、prefix 枚举守卫、畸形 URL | ~32 行 → **17 行** | FileShare.None 占位 + 畸形 URL 两组用例 | business/Channels.md | ✅ **2026-09-19 基本完成**（占用跳过 ×2、畸形 URL ×4、缓存损坏/目录不可写 ×2；残余：prefix 枚举守卫 :117-138 构造困难、翻页边界 :230-263，按变异裁决处理） |
| P3 | Windows CI 腿：提权 740 回退整段（~45 行）、IsExecutableFile true 分支、AppPaths Windows 目录、HpatchzApplier `.exe` 补试 | Windows 专属 | Windows runner 上移除 Assert.Skip 或补平台腿；无法覆盖部分逐行论证保留 | business/Core/SystemProcessRunner.md 等 | ⬜ 未动 |
| P3 | 每日变异冒烟批：核心服务每日小批变异防退化 | CI | 参考 MUTATION.md 的手工流程脚本化（教训：必须构建测试工程） | business/MUTATION.md | ⬜ 未动 |
| P3 | SetDetailActive 路径订阅泄漏（M7 存活论证的残余）：`_ = StartVideoAsync`（:483）异常时 FrameUpdated 退订缺失 | GameItemViewModel :483 | 真机行为验证（Frame 为 null 时无行为差异）；或把订阅/退订收拢到 finally | business/MUTATION.md M7 条目 | ⬜ 未动 |
| P3 | 杂散容错行：JsonException→null 类（GameBackdropService meta、IncrementalUpdateService 暂存清单等，每处 1-3 行）、SeamAnalyzer 防御行、LocalizationService 资源缺失、LaunchSettings 确认更新失败分支（:392-404）、GameItemViewModel Patching/CleaningUp 阶段文案臂（:944-946） | 散布 ~40 行 | 每处 1 用例；或变异验证后按等价类豁免 | business/ 各记录 | ⬜ 未动 |
| P4 | 排除文件可回收（~50 行）：FfmpegLibraryResolver 路径候选、Program hyprctl 输出解析纯函数提取 | 排除文件 | 提取 internal 纯函数后直测 | business/App/Services-Views-Excluded.md | ⬜ 未动 |

## 工具（本地生成，不入库）

```bash
# 未覆盖行清单（当前 ~879 行 / 40 文件）：
dotnet-coverage collect -f cobertura -o out.xml dotnet <测试dll>   # ×4 套件
node artifacts/audit/tools/uncovered-lines.mjs UNCOVERED.md *.cobertura.xml
# 覆盖率汇总 / CI 门禁（已入库）：
node artifacts/audit/tools/coverage-summary.mjs SUMMARY.md *.cobertura.xml
node scripts/coverage-gate.mjs 0.83 <xml...>                       # CI 同款
```

## 新会话必读（harness 教训，重踩成本高）

1. **测试判定口径**：docs/audit/tests/CRITERIA.md——所有测试默认可疑，双向可证伪才算合格。
2. **Dispatch 三规则**（AGENTS.md"UI / 无头测试已知坑"节）：`Dispatch(async)` 禁用（吞断言）；
   `Dispatch(Action)` 必须 await（不 await 则 lambda 没跑）；Bitmap 只能会话线程。
   正确形态先例：BackgroundImageServiceTests / SettingsHeadlessTests（RunToCompletion 泵）。
3. **变异验证**：必须构建**测试工程**（测试 bin 持有独立依赖副本，只构建源项目不生效——
   2026-09-19 Phase 4b-2 实测又踩一次：只构建 src 项目时变异测试照样绿）；测试数据必须能
   区分变异前后（M8 教训：B 服须排首位）。
4. **本机跑测试**：`dotnet test` 可能 0 发现——直跑
   `dotnet tests/<工程>/bin/Debug/net10.0/<程序集>.dll`；单测试用 xunit runner 的 `-method`。
5. **应用运行中锁 bin**（MSB3021/3027）：构建前 `taskkill //F //IM YetAnotherGameLauncher.exe`（Windows）。
6. **每轮改动后**：全量 4 套件 ×2 轮 + `dotnet format whitespace --verify-no-changes` +
   覆盖率复测（基线 83% 门禁已由 CI 强制，本地提前自查）。
7. **Progress&lt;T&gt; 回调异步到达**（2026-09-19 实测）：GameItemViewModel 的进度报告经
   `Progress<UpdateProgress>` 转发，在无同步上下文的测试线程经线程池异步落地——断言进度
   字段必须有界轮询等待（先例：GameItemProgressTests；变异 M10 证明轮询必要而非冗余）。
