# App.Tests 审计汇总

- 测试方法数：234（Fact 225 / Theory 9）；判定：**✅ 171 / ⚠️ 48 / ❌ 17**
- 日期：2026-09-19；判定标准见 `../CRITERIA.md`；机制校准见 `../../phase0/DISPATCH-PROBE.md`

## ❌ 实锤缺陷（17 个，Phase 2 必修）

| 文件 | 数量 | 缺陷 |
|---|---|---|
| BackgroundImageServiceTests | 9 | D1：Dispatch(async) 吞断言（Probe2/3 实锤）+ TempDir 竞态 |
| UiScreenshotTests | 3 | D1 + G8 真实外网 + 裸 Sleep；纱罩像素守卫实际为空 |
| StartupAssetPreloadTests | 2 | D1 + ctx 生命周期竞态 |
| VideoBackdropHeadlessTests | 2 | D7：AssertSurfaceVisible 静默 return 使可见性断言可整组蒸发 |
| NativeUmuLaunchRoutingTests | 1 | D3：Windows CI 零断言静默绿 |

## ⚠️ 存疑（48 个，按缺陷码分布）

| 缺陷码 | 数量 | 说明 |
|---|---|---|
| D2 | 40 | 断言在 Dispatch(Action) 内——Probe1 实锤**当前可传播、测试有效**；违纪形态（AGENTS.md 纪律基于"两类都不可信"的旧认知）。处置：哨兵测试锁定传播属性 + 纪律修订（区分 Action/Func<Task>），不做 31 文件大规模无谓重构 |
| D4 | 10+1 | LinuxFirstRunLaunchTests 整文件 + GameItemActionsTests 1 处：原文 Contains → 反序列化断模型值 |
| D5 | 3 | PlaybackClockTests 墙钟容差（1900-2000/50-100/150-250ms） |
| D5' | 1 | ConfigFailureTests 固定 400ms 后负向断言 |
| D8 | 1 | LaunchErrorOverlayTests.DismissHidesOverlay 纯 VM 层（补真实指针回归） |
| G-环境 | 2 | SettingsAutostartTests 假定本机未开自启（开发机假红） |

## ✅ 正确（171 个）

亮点：MainWindowViewModelTests（29）、UmuComponentProvisionerTests（22）、SeamAnalyzerTests（12）、
LaunchSettingsTests（10）、LocalizationServiceTests（9，含翻译键奇偶守卫）、UmuArchiveExtractionTests（5，
真实 tar 链路+穿越拒绝）、WaylandBackendPolicyTests/WindowStateMapperTests（决策表全格）、
SettingsHeadlessTests（Dispatch 外断言纪律的正面样板）。

## 判定分布（按文件）

| 文件 | 方法数 | ✅ | ⚠️ | ❌ | 记录 |
|---|---:|---:|---:|---:|---|
| BackgroundImageServiceTests | 10 | 1 | 0 | 9 | BackgroundImageServiceTests.md |
| BackgroundResilienceTests | 4 | 4 | 0 | 0 | 同名 .md |
| ConfigFailureTests | 4 | 3 | 1 | 0 | 同名 .md |
| DecodeGuardTests | 6 | 6 | 0 | 0 | 同名 .md |
| DetailPageHeadlessTests | 6 | 0 | 6 | 0 | 同名 .md |
| DispatchProbeTests | 3 | — | — | — | 探针（Phase 0 新增，非存量审计对象） |
| GachaViewModelTests | 3 | 3 | 0 | 0 | 同名 .md |
| GameDetailPresentationTests | 4 | 4 | 0 | 0 | 同名 .md |
| GameDisplayNameTests | 3 | 3 | 0 | 0 | 同名 .md |
| GameItemActionsTests | 8 | 7 | 1 | 0 | 同名 .md |
| LaunchErrorOverlayTests | 4 | 3 | 1 | 0 | 同名 .md |
| LaunchParameterRoundTripTests | 1 | 1 | 0 | 0 | 同名 .md |
| LaunchSettingsPlatformTests | 5 | 5 | 0 | 0 | 同名 .md |
| LaunchSettingsTests | 10 | 10 | 0 | 0 | 同名 .md |
| LinuxFirstRunLaunchTests | 10 | 0 | 10 | 0 | 同名 .md |
| LocalizationServiceTests | 9 | 9 | 0 | 0 | 同名 .md |
| MainWindowHeadlessTests | 6 | 0 | 6 | 0 | 同名 .md |
| MainWindowViewModelTests（3 类） | 29 | 29 | 0 | 0 | 同名 .md |
| NativeUmuLaunchRoutingTests | 1 | 0 | 0 | 1 | 同名 .md |
| NativeUmuUiActionTests | 10 | 10 | 0 | 0 | 同名 .md |
| PlaybackClockTests | 6 | 3 | 3 | 0 | 同名 .md |
| PrerollHandoffTests | 7 | 7 | 0 | 0 | 同名 .md |
| SeamAnalyzerTests | 12 | 12 | 0 | 0 | 同名 .md |
| SettingsAutostartTests | 2 | 0 | 2 | 0 | 同名 .md |
| SettingsHeadlessTests | 4 | 3 | 1 | 0 | 同名 .md |
| SidebarNavHeadlessTests | 11 | 1 | 10 | 0 | 同名 .md |
| StartupAssetPreloadTests | 2 | 0 | 0 | 2 | 同名 .md |
| ThemeHeadlessTests | 3 | 0 | 3 | 0 | 同名 .md |
| ToastHeadlessTests | 1 | 0 | 1 | 0 | 同名 .md |
| ToastTests | 5 | 4 | 1 | 0 | 同名 .md |
| UiScreenshotTests | 3 | 0 | 0 | 3 | 同名 .md |
| UmuArchiveExtractionTests | 5 | 5 | 0 | 0 | 同名 .md |
| UmuComponentProvisionerTests | 22 | 22 | 0 | 0 | 同名 .md |
| VideoBackdropHeadlessTests | 4 | 2 | 0 | 2 | 同名 .md |
| WaylandBackendPolicyTests | 6 | 6 | 0 | 0 | 同名 .md |
| WindowStateMapperTests | 8 | 8 | 0 | 0 | 同名 .md |

非测试文件（无审计对象）：FakeFilePicker / SequentialTestCollection / TestAppBuilder / VmFactory（替身与引导）。
