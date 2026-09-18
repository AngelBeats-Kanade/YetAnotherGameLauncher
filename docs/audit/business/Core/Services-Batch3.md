# Core/Services 逐行审计：IncrementalUpdateService（335 行）+ CompatTools + LinuxPlatformInfo + AppPaths（2026-09-19）

## IncrementalUpdateService.cs —— ✅ 逻辑 / ⚠️ 16 行未覆盖

| 行 | 判定 | 论证 | 覆盖 |
|---|---|---|---|
| 101-119 预下载 | ✅ | EnsureDownloadUrl 纵深 + 暂存结构（patches/files/manifest） | 已覆盖 |
| 122-138 TryLoadStagedManifest | ✅ | 缺文件 null；反序列化往返 | **未覆盖 134-136**（暂存清单损坏 JsonException→null）。Phase 4：写坏 manifest.json 断 null |
| 141-200 ApplyAsync | ✅ | 已应用跳过（DstAlreadyOk 测试）；补丁缺失指引（PatchMissing 测试）；暂存文件落位（AppliesGroups）；暂存清理 | **未覆盖 181-182**（已 OK 的暂存文件跳过——AppliesGroups 用例中 staged files 均需落位，无"已就绪"分支）、**187-188**（暂存缺失 continue——增量清单文件未暂存交给事后修复的分支，GameUpdateServiceTests.Incremental_MissingNewFile 从 Update 层验证了等价行为）。Phase 4：两分支各一行直测 |
| 202-255 ApplyGroupAsync | ✅ | 源缺失全量指引；补丁失败包装；产物缺失/校验不匹配双拦截（CorruptApplierOutput 测试） | **未覆盖 242-243**（补丁器未产出的独立分支——CorruptApplierOutput 走的是 MD5 不匹配 L250 而非文件缺失 L243。Phase 4：FakePatchApplier 增加缺文件输出模式） |
| 271-334 ReplaceWithBackup | ✅ 逻辑 | 在途条目先登记（落位失败也要还原——事故回归专项）；回滚尽力而为 + 失败项拼消息 | 主体已覆盖（RollsBackWholeGroup/InFlightEntryRestored/SecondGroupFailure）；**未覆盖 311-313**（新条目回滚删除分支——现有用例均为 hadOriginal=true）、**315-318**（单文件回滚失败收集——需并发锁构造）。Phase 4：新文件条目 + 制造 Move 失败组合 |

## CompatTools.cs（384 行）—— ✅ 逻辑 / ⚠️ 6 行未覆盖（117-118,130,132-133,135）

通读结论：发现/推荐链/prefix 统一/native-umu 模板生成的决策逻辑全部有直测（CompatTools/Proton/UmuWine/NativeUmuCore 四个测试文件矩阵）。未覆盖行集中在 FindSystemWine 的 `.exe` 补试分支（117-118，Windows 语义——AGENTS.md 实锤项）与 lutris runner 无 bin/ 的边界容错（130-135）。Phase 4：Windows 腿 + 补一例 runner 边界。

## LinuxPlatformInfo.cs（105 行）—— ✅ 逻辑 / ⚠️ 9 行未覆盖（64,70-71,96-98,100-102）

通读结论：GPU 探测路径全注入可测（GpuVendorDetection 九用例）。未覆盖行 = 真实 /proc 文件解析失败容错（64,70-71：文件读到一半被截断/权限）与 IsLinux 之外的平台守卫（96-102：非 Linux 调用方早退——生产由 PlatformInfoFactory 保证不发生）。Phase 4：截断文件用例；平台守卫按"工厂收口"论证保留。

## AppPaths.cs（47 行）—— ✅ 逻辑 / ⚠️ 2 行未覆盖（37-38）

未覆盖 = GetConfigDirectory 的非 Linux 分支（Windows Environment.GetFolderPath）——Windows CI 可达。Phase 4：Windows 腿。

结论：本批无实锤 bug；38 行未覆盖全部列入 Phase 4 清单（其中 2 行 Windows CI 可达）。
