# GameUpdateServiceTests 审计

- 方法数：11；判定：✅ 11
- 形态：FakeChannel/FakeDownloader/FakePatchApplier + 真实文件 + LocalStateService 落盘验证。

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| UpdateAsync_NoLocalVersion_RunsFullSyncAndSavesState (:69) | ✅ | 全量同步 + 状态落盘 |
| UpdateAsync_StateBelongsToOtherGame_TreatedAsNotInstalled (:85) | ✅ | 他人 state.json 不误判为已安装 |
| UpdateAsync_LocalVersionInPatches_RunsIncremental (:102) | ✅ | 增量链路 + 补新文件 + 状态升级 |
| UpdateAsync_Incremental_MissingNewFile_RepairedByFullManifest (:139) | ✅ | 增量未覆盖的新文件由全量清单补下（RepairedFiles=1） |
| PredownloadAsync_NoWindow_Throws (:173) | ✅ | 无预下载窗口抛 UpdateException |
| PredownloadAsync_NoMatchingPatch_ThrowsWithGuidance (:185) | ✅ | 补丁源版本不匹配给可读指引 |
| PredownloadAsync_Available_StagesIncrementalContent (:203) | ✅ | 预下载暂存不动物资本体 |
| ApplyPredownloadAsync_NothingStaged_Throws (:232) | ✅ | 无暂存应用抛异常 |
| ApplyPredownloadAsync_AppliesStagedAndSavesState (:239) | ✅ | 两段式预下载-应用闭环 |
| UpdateAsync_PackageManifest_ExtractsAndSavesState (:272) | ✅ | 包式渠道解压落盘 |
| PredownloadThenApply_PackageChannel_ExtractsStagedArchive (:292) | ✅ | 包式预下载暂存→应用解压 |
