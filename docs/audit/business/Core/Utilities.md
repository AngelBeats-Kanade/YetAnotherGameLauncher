# Core/Utilities 逐行审计（2026-09-19）

## Hashing.cs（29 行）—— ✅ 逻辑 / ⚠️ 覆盖缺口

| 行 | 判定 | 论证 | 覆盖 |
|---|---|---|---|
| 9-10 Md5Hex(byte[]) | ✅ | 标准向量测试（90015098...） | 已覆盖 |
| 13-17 Md5Hex(string) | ✅ | 流式与内存哈希一致（随机 4KB 对照） | 已覆盖 |
| 20-28 Sha256HexAsync | ✅ 逻辑 / ⚠️ 未覆盖 21,24,26-28 | `Convert.ToHexStringLower` + `HashDataAsync(ct)` + `ConfigureAwait(false)`（Core 层 CA2007 正确）；唯一疑点 L23-25 的 CA2007 pragma 注释自洽 | **未覆盖**——仅被 FfmpegLibraryResolver:206 与 UmuComponentProvisioner:1122 调用，两处均在测试中未走真实路径。**Phase 4 补测**：临时文件 + 已知向量 |

## FileUtilities.cs（176 行）—— ✅ 逻辑 / ⚠️ 覆盖缺口（29 行未覆盖）

| 行 | 判定 | 论证 | 覆盖 |
|---|---|---|---|
| 9-39 WriteAtomicAsync | ✅ 逻辑 | tmp+Move 原子写；只读解除后重试一次（Windows 占用语义）；二次失败包装可操作 IOException | 主体已覆盖（WriteAtomicAsync_ReadOnlyTarget_Overwritten）；**未覆盖 33,38**（重试仍失败→包装异常路径）。Phase 4：占位句柄构造二次失败 |
| 42-51 DeleteQuiet | ✅ | 尽力删除语义正确 | 已覆盖（TryDeleteDirectory 测试链路） |
| 54-64 IsReparsePoint | ✅ 逻辑 | 探测失败按否（后续删除自兜） | **未覆盖 60-62**（catch 分支——GetAttributes 抛 IO/Unauthorized 时按 false）。Phase 4：竞态窗口难稳定构造，可在 FileUtilitiesTests 用不存在路径+注入？不可注入——记录为容错分支，Phase 4 尝试或经变异验证判定等价变异豁免 |
| 67-88 TryDeleteLinkQuiet | ✅ 逻辑 | 先按目录删链接、失败按文件删；再失败 false | **未覆盖 68,70-72,74-76,79-81,83-84,86,88**——链接删除分支在 Core.Tests 未直接测（GameInstallServiceTests 的链接测试走的是"不穿链接"语义而非删除链接本身）。Phase 4：TryDeleteDirectory 对符号链接/junction 的直接用例 |
| 96-144 TryDeleteDirectory | ✅ 逻辑 | 自底向上尽力删；reparse 不穿透；残留返回 false——语义被 TryDeleteDirectory_UndeletableChild 双平台用例验证 | 主体已覆盖；**未覆盖 109,111-113,115**（reparse-point 删除分支本体）。Phase 4 同上 |
| 147-152 SanitizeGameId | ✅ | 正则断言（launch-wuthering-waves-global-cn-时间戳.log） | 已覆盖；**未覆盖（无）**——空清洗回退 "game" 分支在 149-151 内但 151 未单独断言（变异验证时补） |
| 155-175 IsExecutableFile | ✅ 逻辑 | Windows 存在即可；Linux 校验 UserExecute 位 | **未覆盖 163-164**（Windows true 分支——Linux CI 天然不可达，Windows CI 可达）、**171-173**（catch 按否）。Phase 4：Windows 腿 + 无权限文件用例 |

结论：无实锤 bug；两文件共 34 行未覆盖，全部列入 Phase 4 补测清单（含 2 个 Windows-CI 可达分支）。
