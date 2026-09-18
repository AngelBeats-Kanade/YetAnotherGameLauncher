# UmuArchiveExtractionTests 审计

- 方法数：5；判定：✅ 5
- 形态：直调 internal（InternalsVisibleTo）；System.Formats.Tar 构造与线上同链路 tar.gz；
  穿越拒绝断言双平台有效（:52-59 的 Linux 专属执行位/软链检查是**叠加**深度而非静默跳过，
  其余断言双平台全跑——与 D3 早退有本质区别）。

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| ExtractTarArchive_RestoresModesAndLinks_SkipsTraversal (:22) | ✅ | 目录/文件还原 + 内容正确 + ../条目拒绝（目标内外双断言）+ Linux 执行位/软链还原 |
| ExtractTarArchive_HardLinkWithMissingTarget_SkipsSilently (:63) | ✅ | 硬链接目标缺失：跳过且不抛 |
| ExtractSingleTopLevel_MovesTopDirIntoTarget (:80) | ✅ | 顶层目录包迁移进目标（无 .extract 残留） |
| ExtractSingleTopLevel_FlatArchive_FlattensIntoTarget (:99) | ✅ | 无顶层目录包摊开落 target 根 |
| ExtractTarArchive_LinkTargetEscapingDestination_IsSkipped (:117) | ✅ | 链接目标穿越/绝对路径拒绝 + 文件条目就地落盘（防链接写穿回归） |
