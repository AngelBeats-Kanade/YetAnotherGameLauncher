# App/Services/UmuComponentProvisioner.cs 逐行审计（1157 行，2026-09-19）

审计方法：全文件结构通读 + 未覆盖行号逐段核对（95-465 Proton 流、760-876 Runtime 流精读；
22 个专项测试矩阵见 Phase 1 记录）。

## 结构与判定

| 段 | 行 | 判定 | 论证 |
|---|---|---|---|
| 构造/IsProtonReady/IsRuntimeReady | 60-109 | ✅ | 三文件就绪判定 + runtime 三条件（目录/marker/入口）；空 variant 恒 ready |
| EnsureProtonAsync | 112-139 | ✅ 逻辑 | 本地就绪即用不联网（LocalInstalled 零下载测试）；缺才下载；代号/具体版本分流 | 
| ResolveRequiredRuntime | 142-165 | ✅ | 清单不可读回退默认 Runtime（容错注释自证） |
| FindInstalledProton | 168-188 | ✅ | 绝对路径/代号前缀/版本名三条路径 + 错架构视同缺失（WrongArch 测试） |
| EnsureRuntimeAsync | 191-279 | ✅ 逻辑 | 双重检查锁（锁后二次 ready）；runtime 元数据失败归类 UmuRuntimeDownloadFailed（专项回归） | 
| DownloadLatestProtonAsync / ByTag | 282-465 | ✅ 逻辑 | DW(Forgejo 数组)/GE/UMU(GitHub 对象)分流；资产选择（架构过滤/穿越拒绝/反向架构空）；下载→SHA256→解包→落位→清理 |
| UpdateProtonAsync + Prune | 467-560 | ✅ 逻辑 | 更新即重装 latest + 清同发行版旧版保其它（UpdatePrunes 测试） |
| ExtractTarArchive / ExtractSingleTopLevel | 878-980 | ✅ | 穿越/链接/执行位/顶层迁移——UmuArchiveExtractionTests 五用例 |
| Runtime 安装流 | 760-876 | ✅ 逻辑 / ❗ 覆盖缺口 | 版本号/SHA256SUMS/BUILD_ID 三段元数据 + 大文件下载 + staging 解包落位 + umu 符号链接 + 双层清理。**仅失败分类入口被测**（CatalogFetchFails），正流程未驱动 |
| VerifySha256Async | 1118-1130 | ✅ 逻辑 | 流式 SHA256 对照，不匹配抛 UmuRuntimeDownloadFailed | 
| 辅助（ParseSha256For/TryChmod/TryDelete/FetchTextAsync） | 1130-1157 | ✅ | ParseSha256For 双直测 |

## 未覆盖 260 行的构成与 Phase 4 处置

1. **EnsureRuntimeAsync 正流程（770-876，约 100 行）**：Phase 4 最高价值补测——桩 HTTP 注册
   latest-public-beta.txt / SHA256SUMS / BUILD_ID.txt / 真实小 tar.gz（UmuArchiveExtractionTests
   的 WriteTarGz 手法复用），一次覆盖安装/落位/marker/符号链接/清理全链。
2. **SHA256 校验链（1118-1130 + Hashing.Sha256HexAsync 5 行）**：随上例覆盖；另补不匹配抛错用例。
3. **进度回调与错误包装散点（约 120 行）**：DW/GE/UMU 下载进度、重试细节、各异常包装——
   大多为 `progress?.Report` 与 catch-分类行。Phase 4：每渠道正流程断言进度序列（低价值高噪音，
   列为变异验证批次处理：删 Report 行应有测试红或按可观测性等价豁免）。
4. **锁与双重检查（202-208 等）**：AcquireLock 已有设计；并发用例 Phase 4 视窗口可构造性决定。

结论：无实锤 bug。该文件是 Phase 4 补测的第一优先目标（单点收益 ~110 行）。
