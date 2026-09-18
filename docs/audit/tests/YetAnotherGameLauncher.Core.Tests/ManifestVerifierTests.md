# ManifestVerifierTests 审计

- 方法数：11；判定：✅ 11
- 形态：真实文件 + 纯校验；穿越/绝对路径按平台取扎根形式（双腿各跑各的正确断言）。

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| Verify_ReportsPerFileProgress (:27) | ✅ | 逐文件进度回调精确序列 |
| VerifyFast_AllFilesOk (:48) | ✅ | 快速校验全过 |
| VerifyFast_MissingFileReported (:67) | ✅ | 缺失报告 Missing |
| VerifyFast_SizeMismatchReported (:84) | ✅ | 尺寸不符报告 |
| VerifyFast_SameSizeWrongContent_Passes (:101) | ✅ | 快速校验盲区语义（同尺寸过）——与全量 MD5 兜底成对 |
| VerifyFull_SameSizeWrongContent_Md5MismatchReported (:116) | ✅ | 全量 MD5 抓同尺寸损坏 |
| VerifyFull_AllOk (:132) | ✅ | 全量全过 |
| Verify_PathOutsideInstallDir_Throws (:148) | ✅ | ../ 拒绝 |
| Verify_AbsolutePathInManifest_Throws (:161) | ✅ | 绝对路径拒绝（平台扎根形式） |
| Verify_WindowsSeparatorNormalized (:176) | ✅ | 反斜杠条目名归一 |
| NeedsDownload_ContainsAllBrokenFiles (:193) | ✅ | 损坏集合聚合（排序后精确比对） |
