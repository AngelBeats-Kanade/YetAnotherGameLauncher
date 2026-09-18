# IncrementalUpdateServiceTests 审计

- 方法数：12；判定：✅ 12
- 形态：FakeDownloader/FakePatchApplier + 真实文件；回滚/事务语义断言到字节级与目录残留级。

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| PredownloadAsync_StagesPatchesFilesAndManifest (:59) | ✅ | 暂存目录结构（patches/files/manifest） |
| PredownloadAsync_ManifestPathEscapingSandbox_Rejected (:81) | ✅ | ../ 暂存穿越拒绝（未写出暂存目录） |
| SafeJoin_RootedPath_Rejected (:98) | ✅ | 绝对路径与 ..\ 拒绝 |
| ApplyAsync_AppliesGroupsAndReplacesFiles (:105) | ✅ | 组应用 + 替换 + 补丁器收到旧相对路径 + 暂存清理 |
| ApplyAsync_SkipsGroupWhenDstAlreadyOk (:126) | ✅ | 目标已就绪跳过（零补丁调用） |
| ApplyAsync_PatchMissing_ThrowsWithGuidance (:142) | ✅ | 补丁未预载指引 |
| ApplyAsync_MissingSrcFile_ThrowsWithFullSyncGuidance (:158) | ✅ | 源缺失 → 全量更新指引 |
| ApplyAsync_CorruptApplierOutput_ThrowsBeforeReplace (:174) | ✅ | 坏输出在替换前拦截、本体完好 |
| ApplyAsync_ReplaceFailure_RollsBackWholeGroup (:192) | ✅ | 整组回滚 + 无 .yagl-bak 残留 |
| ApplyAsync_SecondGroupFailure_KeepsFirstGroupApplied (:217) | ✅ | 组间事务边界：第一组保留、第二组回滚 |
| ReplaceWithBackup_PlaceMoveFails_InFlightEntryRestored (:240) | ✅ | 落位失败的在途条目也要还原（修复前缺失文件+bak 孤悬的事故回归） |
| StagedManifest_RoundTrips (:271) | ✅ | 暂存清单序列化往返 |
