# PackageInstallerServiceTests 审计

- 方法数：12；判定：✅ 12
- 亮点：zip 条目沙箱四连回归（反斜杠名/..文件/../目录/扎根目录）+ 只读目标覆盖（Windows 语义）
  + 预下载暂存零重下回归。

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| InstallAsync_DownloadsAndExtractsArchive (:34) | ✅ | 下载解压 + 临时包目录清理 |
| Predownload_StagesArchiveWithoutExtracting (:47) | ✅ | 暂存不解压 + 暂存清单写入 |
| PredownloadThenApply_ExtractsIntoInstallDir (:61) | ✅ | 两段式闭环 |
| ApplyPredownload_UsesStagedArchive_WithoutReDownloading (:74) | ✅ | 应用阶段零下载（曾误走整包重下的事故回归） |
| ApplyPredownload_CorruptStagedArchive_FallsBackToReDownload (:89) | ✅ | 暂存包损坏 → 重下兜底 |
| InstallAsync_Md5Mismatch_Throws (:106) | ✅ | 真实下载器 + 桩 HTTP 的 md5 校验链路 |
| InstallAsync_BackslashEntryNames_ExtractIntoNestedDirectories (:119) | ✅ | dotnet/runtime#98247 回归（\ 条目名在 Unix 解成嵌套目录而非平铺垃圾） |
| InstallAsync_EntryEscapingSandbox_ThrowsAndWritesNothing (:134) | ✅ | .. 文件条目拒绝且零写入 |
| InstallAsync_DirectoryEntryEscapingSandbox_ThrowsAndCreatesNothing (:149) | ✅ | .. 目录条目早退漏洞回归 + 恶意条目在后不再解压 |
| InstallAsync_RootedDirectoryEntry_IsContainedInInstallDir (:164) | ✅ | / 开头目录条目收容进安装目录（Path.Combine rooted 陷阱回归） |
| InstallAsync_BenignDirectoryEntries_StillExtractFiles (:180) ✅ | | 正常目录条目不受收紧影响 |
| InstallAsync_ExistingReadOnlyTarget_Overwritten (:192) | ✅ | 只读目标解除属性后覆盖（Windows 占用语义防线） |
