# Core.Tests 审计汇总

- 测试方法数：238（Fact 231 / Theory 7，含 Theory 数据展开后执行数 245）；判定：**✅ 233 / ⚠️ 2 / ❌ 2**（另有 1 个 ⚠️ 在 App.Tests 计）
- 更正：Core.Tests 方法数 = 244（Fact 237 / Theory 7：UpdatePlanner 5+VersionComparison 3、Kuro 等不计）。
  精确口径见各文件记录；本 INDEX 按"文件 × 方法"合计 244。判定：✅ 239 / ⚠️ 2 / ❌ 2 / 1 平台双腿✅。
- 日期：2026-09-19；标准见 `../CRITERIA.md`

## ❌（2，Phase 2 必修）

| 文件 | 缺陷 |
|---|---|
| SystemProcessRunnerTests.RunAsync_FireAndForget_WritesOutputToLogFile (:31) | D3：Windows CI 零断言静默绿；日志落盘行为跨平台可测，应补 Windows 腿 |

## ⚠️（2）

| 文件 | 缺陷 |
|---|---|
| GameLauncherServiceTests.BuildPlan_AbsoluteRuntimeWithoutExecBit_IsFixedAutomatically (:147) | D3'：POSIX 专属行为 Windows 静默早退（无 Windows 对应语义，改 Assert.Skip 显式化） |
| HttpFileDownloaderTests.DownloadFileAsync_ReportsMonotonicProgress (:224) | G3-lite：测试名承诺"单调"但只断言末值，非单调回归拦不住（加相邻报告两两 ≤ 断言） |

## ✅（240）——按文件

| 文件 | 方法数 | 记录 |
|---|---:|---|
| InstallPathTests | 3 | InstallPathTests.md |
| Models/GameCatalogJsonTests | 9 | GameCatalogJsonTests.md |
| Models/GameCatalogValidationTests | 17 | GameCatalogValidationTests.md |
| Services/AutostartContentTests | 2 | UtilitiesTests.md |
| Services/CompatToolsTests | 3 | CompatToolsTests.md |
| Services/GameBackdropServiceTests | 16 | GameBackdropServiceTests.md |
| Services/GameCatalogServiceTests | 14 | GameCatalogServiceTests.md |
| Services/GameInstallServiceTests | 11 | GameInstallServiceTests.md |
| Services/GameLauncherServiceTests | 15 | GameLauncherServiceTests.md（含 1⚠️） |
| Services/GameUpdateServiceTests | 11 | GameUpdateServiceTests.md |
| Services/GpuVendorDetectionTests | 9 | GpuVendorDetectionTests.md |
| Services/HttpFileDownloaderTests | 17 | HttpFileDownloaderTests.md（含 1⚠️） |
| Services/IncrementalUpdateServiceTests | 12 | IncrementalUpdateServiceTests.md |
| Services/LinuxAutostartServiceTests | 3 | AutostartServiceTests.md |
| Services/LinuxPlatformInfoTests | 3 | PlatformInfoTests.md |
| Services/ManifestVerifierTests | 11 | ManifestVerifierTests.md |
| Services/NetworkProxyManagerTests | 4 | NetworkProxyManagerTests.md |
| Services/PackageInstallerServiceTests | 12 | PackageInstallerServiceTests.md |
| Services/PlatformInfoFactoryTests | 1 | PlatformInfoTests.md |
| Services/ProtonCompatTests | 10 | CompatToolsTests.md |
| Services/SpeedLimiterTests | 3 | SpeedLimiterTests.md |
| Services/SystemProcessRunnerTests | 5 | SystemProcessRunnerTests.md（含 1❌） |
| Services/Umu/NativeUmuCoreTests | 13 | NativeUmuCoreTests.md |
| Services/Umu/NativeUmuLauncherLaunchTests | 1 | NativeUmuLauncherLaunchTests.md |
| Services/UmuWineCompatTests | 11 | UmuWineCompatTests.md |
| Services/UpdatePlannerTests + VersionComparisonTests | 8 | UmuWineCompatTests.md |
| Services/WindowsAutostartServiceTests | 4 | AutostartServiceTests.md |
| Utilities/FileUtilitiesTests | 4 | UtilitiesTests.md |
| Utilities/HashingTests | 2 | UtilitiesTests.md |

## 总体评价

Core.Tests 是全仓质量最高的测试集：真实文件系统 + 注入点（home/proc/sysfs/PATH/dataHome/
hostArchitecture）实现跨平台确定性；安全回归密集（zip/tar 沙箱、穿越拒绝、prefix 保留、
链接不穿透、只读/占用容错）；回滚事务断言到目录残留级。发现的问题只有 2 处平台早退形态 + 1 处断言弱于命名。
