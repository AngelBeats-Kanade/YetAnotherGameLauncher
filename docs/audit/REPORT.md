# 全仓测试与代码双向审计总报告（REPORT）

- 审计执行：2026-09-19（单日完成 Phase 0-5；证据链见 git 历史与 `docs/audit/` 全部记录）
- 基点：a333e07 → 本报告提交；每阶段独立 commit，记录先于修复，可逐个审计/回退

## 一、结论（TL;DR）

| 维度 | 结果 |
|---|---|
| 测试审计 | 527 个测试方法逐一判定：✅459 / ⚠️51 / **❌17（实锤假绿，全部修复）** |
| Dispatch 行为校准 | 三探针实锤：`Action` 同步 lambda **不吞断言**（旧纪律系过度泛化）；`Func<Task>` **全形态吞断言**；不 await 则 lambda 未执行；Bitmap 解码仅会话线程可用 |
| 业务逐行审计 | 全部 91 个源文件（~1.5 万行）通读完毕：**已覆盖代码无实锤 bug**；1132 行未覆盖逐行定位并给出处置 |
| async/await 专项 review | 4 项发现全部修复（fire-and-forget 异常逃逸 ×2、async void 崩溃风险、sync-over-async 前提注释） |
| 补测（Phase 4a） | 新增 40 个用例（幂等矩阵/Runtime 安装/设置命令组/GameItem 错误矩阵/LaunchSettings 矩阵/回归用例） |
| 变异验证（Phase 4b） | 8 个单点变异抽查：**7 击杀 / 1 等价防御存活（论证在案）** |
| 行覆盖 | 80.29%（基线，首次真实测量）→ **83.49%**；剩余 949 行缺口全部逐行归因（见下） |
| 终验 | 全量 610+ 测试执行多轮全绿；零警告构建；format 干净；CI 增覆盖率门禁与禁用形态守卫 |

## 二、修复清单

### 假绿测试（❌17）
- BackgroundImageServiceTests 9 个 + StartupAssetPreloadTests 2 个 + UiScreenshotTests 3 个：
  `Dispatch(async)` 吞断言 → 统一重构为 `await Dispatch(同步 lambda)` + RunJobs 泵（RunToCompletion 形态），断言全部外移
- VideoBackdropHeadlessTests：AssertSurfaceVisible 静默 return → Assert.NotNull 硬失败
- NativeUmuLaunchRoutingTests：Windows 零断言早退 → Windows 腿反向断言平台门控
- SystemProcessRunnerTests 日志用例：Windows 腿补齐（cmd 双路输出）

### 测试暴露的真实缺陷（假绿掩盖的）
- 版本变化后的背景重解析需先 ResetVersionCheckCache（会话缓存语义）
- schema-4 裸模板实际会被升级为 native-umu（原测试弱断言 `Contains("{exe}")` 放行；测试意图已按生产语义修正）

### 业务代码（async review）
- LocalStateService.Load：IO 占用异常穿出 → 归入"状态未知"返回 null
- GameItemViewModel.RefreshAsync：catch 补 OperationCanceledException
- StartVideoAsync：解码器启动失败 → catch 回退海报（原为未观察异常）
- MainWindow.PlayAsync：async void 补全量兜底（原非取消异常即进程崩溃）
- PersistWindowState：GetResult 安全前提注释固定

## 三、防复发机制（本次落地）

1. **DispatchSentinelTests**：锁定 `Dispatch(Action)` 异常传播属性，框架升级引入吞断言即红
2. **CI grep 守卫**：`Dispatch(async` 禁用形态（注释豁免词除外）入仓即红
3. **CI 覆盖率门禁**：每轮 CI 采集覆盖率，行覆盖低于基线（83%）即红（见 ci.yml）
4. **审计记录入库**：`docs/audit/`（判定标准/探针结论/527 测试判定/91 文件逐行审计/变异击杀表）

## 四、剩余缺口归因（949 行 = 100% - 83.49%）

| 类别 | 约行数 | 说明 |
|---|---:|---|
| `[ExcludeFromCodeCoverage]` 排除文件（不在 5749 基数内，另计 ~2600 行） | — | FFmpeg 原生互操作/组合根/系统对话框——逐段结构性理由见 Services-Views-Excluded.md |
| Windows CI 专属（本平台结构性不可达） | ~80 | 提权 740 回退、执行位 Windows 分支、AppPaths 等；Assert.Skip 显式化或 Windows 腿待补 |
| 竞态/容错等价类 | ~120 | 双层防御、释放竞态、ObjectDisposed 容错——确定性测试不可达；变异抽查 M7 已示范论证方法 |
| 进度回调散点（UmuComponentProvisioner） | ~120 | `progress?.Report` 行——删行无可观测差异，按可观测性等价豁免；正流程已补测 |
| 尚未补测的功能分支 | ~250 | Phase 4 清单未竟项（GameItem 进度卡生命周期、LaunchSettings 残余、KuroGacha 容错、杂散 JsonException 分支）；每行均有记录与方案 |
| 测试基建改进依赖项 | ~60 | 需 FakeChannel 失败注入、FilePicker headless 等，随用例落地 |

诚实边界：83.49% 行覆盖 + 变异抽查 ≠ 无 bug 证明。等价变异、多变异叠加、真机行为（Wayland/HDR/原生解码）
不在单测能力范围内——后者以 UiScreenshotTests 像素断言 + 真机冒烟清单（DEVELOPMENT.md）部分覆盖。

## 五、可复现命令

```bash
dotnet build -warnaserror                                  # 零警告门禁
dotnet tests/<工程>/bin/Debug/net10.0/<程序集>.dll          # 直跑测试（MTP 模式）
dotnet-coverage collect -f cobertura -o out.xml dotnet <测试dll>   # 覆盖率
node artifacts/audit/tools/coverage-summary.mjs out.md *.cobertura.xml    # 汇总
node artifacts/audit/tools/uncovered-lines.mjs uncovered.md *.cobertura.xml  # 未覆盖行清单
```

## 六、阶段产物索引

- Phase 0：`docs/audit/phase0/`（BASELINE / DISPATCH-PROBE）
- Phase 1：`docs/audit/tests/`（CRITERIA + 4 工程 INDEX + 82 文件记录）
- Phase 2：commit `fix(async)` 前一笔 `test: fix 17 false-green...`
- Phase 3：`docs/audit/business/`（Core INDEX + 10 记录 / Channels.md / App 3 记录）
- Phase 4：`docs/audit/business/MUTATION.md` + 新增测试文件 ×6
- Phase 5：本报告 + ci.yml 门禁 + DEVELOPMENT.md §9 数据更新
