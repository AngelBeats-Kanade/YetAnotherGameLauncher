# Phase 0 探针实验：Dispatch 各重载对 lambda 内断言失败的传播行为

- 实测日期：2026-09-19（Avalonia 12.1.2 / xunit.v3 4.0.0 / .NET 10.0.11，本机 Linux）
- 探针代码：`tests/YetAnotherGameLauncher.App.Tests/DispatchProbeTests.cs`（3 个探针，按设计都必须失败）
- 方法：`Assert.Fail` 与其他 `Assert.*` 失败时抛同一种 FailException，传播行为等价；
  每个探针用 xunit runner 的 `-method` 单独运行，看套件摘要里 Failed 计数。

## 结果

| 探针 | 重载 | 断言位置 | 结果 | 结论 |
|---|---|---|---|---|
| Probe1 | `Dispatch(Action)` | 同步 lambda 内 | **Failed: 1（真红）** | 断言失败**正常传播**，不吞 |
| Probe2 | `Dispatch(Func<Task>)` | await 之前 | Failed: 0（假通过） | **被吞**：异常被 async 状态机捕获进返回 Task，该 Task 被 Dispatch 丢弃 |
| Probe3 | `Dispatch(Func<Task>)` | 真 `await Task.Delay` 之后 | Failed: 0（假通过） | **被吞**（与 AGENTS.md 2026-09 记载一致） |

## 对 Phase 1 审计的定级影响

1. **`Dispatch(async () => ...)` 形态（14 个测试）= 实锤假绿**：
   lambda 内任何断言失败都永远无法使测试变红（await 前吞、await 后既吞且可能根本不执行）。
   涉及：BackgroundImageServiceTests 前 9 个、UiScreenshotTests 3 个、StartupAssetPreloadTests 2 个。
   另有未定论的次生风险：被丢弃的续体成为孤儿 fire-and-forget，与测试 teardown（TempDir.Dispose 等）竞态。
2. **`Dispatch(() => ...)` 同步形态（约 31 个测试）≠ 假绿**：Probe1 证明断言失败会传播。
   它们违反 AGENTS.md「断言一律放 Dispatch 之外」的纪律（该纪律对 Action 重载而言是过度保守的防御），
   定级为 ⚠️ 违纪但当前有效——Phase 1 逐一确认无 async 混入后按 ⚠️ 处理，Phase 2 统一外移断言以对齐纪律。
3. AGENTS.md 需更新：把「Action 重载内断言可传播（Probe1 实测）」与「Func<Task> 全形态禁用（Probe2/3 实测）」区分开，
   消除现在"两类都不可信"的模糊表述——在 Phase 2 落哨兵测试时一并改。

## 哨兵化（Phase 2 执行）

三个探针转正为常驻哨兵 `DispatchProbeTests`（改名 Sentinel，语义取反为守卫传播）：
任何环境升级（Avalonia/ xunit.v3）导致 Action 重载开始吞断言，CI 立即红。
