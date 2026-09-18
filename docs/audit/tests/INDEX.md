# Phase 1 测试审计总索引（527 个测试方法全量判定完毕）

- 日期：2026-09-19；标准 `CRITERIA.md`；机制校准 `../phase0/DISPATCH-PROBE.md`
- 总判定：**✅ 459 / ⚠️ 51 / ❌ 17**（合计 527；按方法计，Theory 按方法不计案例）

## 各工程

| 工程 | 方法数 | ✅ | ⚠️ | ❌ | INDEX |
|---|---:|---:|---:|---:|---|
| App.Tests | 234 | 171 | 48 | 17 | YetAnotherGameLauncher.App.Tests/INDEX.md |
| Core.Tests | 244 | 240 | 2+1D3'⚠️(计入App表外另有1) | 1 | YetAnotherGameLauncher.Core.Tests/INDEX.md |
| Channels.Kuro.Tests | 45 | 44 | 1 | 0 | YetAnotherGameLauncher.Channels.Kuro.Tests/INDEX.md |
| Channels.Hypergryph.Tests | 17 | 17 | 0 | 0 | YetAnotherGameLauncher.Channels.Hypergryph.Tests/INDEX.md |

注：Core.Tests 的 2 个 ⚠️ 为 GameLauncherServiceTests:147（POSIX 专属早退）与
HttpFileDownloaderTests:224（断言弱于命名）；Kuro 的 1 ⚠️ 为 HpatchzApplierTests:105（同 POSIX 早退）。

## ❌ 全清单（17 个，Phase 2 修复对象）

1-9. BackgroundImageServiceTests 前 9 个 —— D1（Dispatch(async) 吞断言，Probe 实锤）
10-11. StartupAssetPreloadTests 2 个 —— D1 + ctx 生命周期竞态
12-14. UiScreenshotTests 3 个 —— D1 + G8 真实外网 + 纱罩像素守卫实际为空
15-16. VideoBackdropHeadlessTests :30/:131 —— D7（AssertSurfaceVisible 静默 return）
17. NativeUmuLaunchRoutingTests :26 —— D3（Windows CI 零断言静默绿）

## ⚠️ 修复计划（Phase 2 一并处理的部分）

- D4（11）：LinuxFirstRunLaunchTests 全文件 + GameItemActionsTests:170 → 反序列化断模型值
- D5（3+1）：PlaybackClockTests 墙钟 → 查 TimeProvider 可注入性；ConfigFailureTests:64 → 有界条件轮询
- G3-lite（1）：HttpFileDownloaderTests:224 → 相邻报告两两单调断言
- D8（1）：LaunchErrorOverlayTests → 补真实指针关闭回归
- D1 变体（1）：SettingsHeadlessTests:142 → IsType 移出 lambda
- D3'（3）：GameLauncherServiceTests:147、HpatchzApplierTests:105、（SystemProcessRunner 补腿归 ❌1）→ Assert.Skip 显式化
- D2（40）：**不做大规模重构**。依据：Probe1 实锤 Dispatch(Action) 断言可传播、全部有效；
  AGENTS.md 纪律基于 Func<Task> 事故的过度泛化。处置 = 三探针转正常驻哨兵（传播属性被破坏即 CI 红）
  + AGENTS.md 纪律修订（Action 同步块可用、Func<Task> 全禁），以最小扰动消除风险。
  SettingsAutostartTests（G-环境，2）：Windows 注册表真实查询需注入点，属生产代码改动 → 移交 Phase 4。

## 假绿率口径

"断言失败无法使测试变红"的实锤假绿 = 17/527 ≈ 3.2%（全部集中在 App.Tests 的 Dispatch(async)
形态与静默早退形态）。其余 51 个 ⚠️ 为有效性不受损的形态/脆弱性缺陷。
