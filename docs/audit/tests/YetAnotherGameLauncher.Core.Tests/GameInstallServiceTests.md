# GameInstallServiceTests 审计

- 方法数：11；判定：✅ 11
- 亮点：:74 目录链接不穿透（Windows junction / Linux 双腿都真实执行）；:214 compatdata/prefix
  保留名单回归（AGENTS.md"删 prefix 等于毁环境"）；:231 单个删除失败不中断（双平台各自语义断言）。

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| SyncAsync_DownloadsAllMissingFiles (:31) | ✅ | 清单全量下载（含子目录） |
| SyncAsync_SkipsAlreadyValidFiles (:46) | ✅ | 有效文件跳过（请求列表精确） |
| SyncAsync_ReplacesCorruptedFile (:61) | ✅ | 损坏文件替换 |
| SyncAsync_Cleanup_DoesNotFollowDirectoryLinks (:74) | ✅ | 清理游离文件不穿目录链接（词法路径删穿链接事故回归） |
| SyncAsync_SameSizeCorruption_RepairedOnSecondPass (:123) | ✅ | 同尺寸损坏由 MD5 事后校验兜底 |
| SyncAsync_PostVerificationFailure_Throws (:141) | ✅ | 下载内容与清单 MD5 不符抛 UpdateException |
| SyncAsync_MissingUrl_Throws (:155) | ✅ | 清单缺 Url 抛异常 |
| SyncAsync_ReportsDoneProgress (:169) | ✅ | Done 进度（有界 SpinWait 等异步投递） |
| SyncAsync_CleansStaleFilesButPreservesSavedAndYagl (:192) | ✅ | 游离清理 + Saved/.yagl/下载配置保留 |
| SyncAsync_NeverDeletesWinePrefixCompatdata (:214) | ✅ | 旧版 prefix 位置绝不被清 |
| SyncAsync_SingleDeletionFailure_DoesNotAbortSync (:231) | ✅ | 只读目录删除失败仅跳过，其余照常（平台分支双腿各有断言） |
