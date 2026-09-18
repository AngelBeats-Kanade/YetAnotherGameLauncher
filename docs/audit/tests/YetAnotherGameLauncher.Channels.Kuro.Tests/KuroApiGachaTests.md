# KuroChannelApiTests / KuroGachaServiceTests 审计

## KuroChannelApiTests（9 ✅）
真实 JSON fixture + 假下载器；MD5 防篡改链路用真实下载器 + 桩 HTTP 端到端验证。

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| GetVersionInfo_ParsesVersionPatchSourcesAndPredownload (:88) | ✅ | index.json 全字段（版本/补丁源/预下载窗口） |
| GetVersionInfo_PredownloadClosed_WhenBlockMissing (:102) | ✅ | 预下载块缺失 → 关闭 |
| GetVersionInfo_MissingIndexUrlOption_Throws (:113) | ✅ | 缺 indexUrl 抛 UpdateException |
| GetManifest_BuildsFilesWithCdnUrlsAndEscaping (:122) | ✅ | 清单 URL 拼接 + 空格转义 + fromFolder 覆盖 |
| GetManifest_IndexFileMd5Mismatch_ThrowsVerificationException (:145) | ✅ | 清单防篡改端到端 |
| GetIncrementalManifest_MatchesExactSourceVersion (:160) | ✅ | 差分组/双 baseUrl 语义 |
| GetIncrementalManifest_UnknownSourceVersion_ReturnsNull (:202) | ✅ | 未知源版本 null |
| CdnSelector_PicksHighestPriorityWithK1K2 (:212) | ✅ | CDN 选择（P 最高且 K1=K2=1） |
| PatchUrl_FallsBackToDefaultBaseUrlThenResources (:225) | ✅ | 补丁 URL 三级回退 |

## KuroGachaServiceTests（9 ✅）
XOR 混淆互逆 fixture（:25，与官方算法互逆而非抄生产代码）+ 路径候选 + 请求体形状 + 缓存合并。

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| TryExtractGachaUrl_DecryptedLog_ParsesAllParams (:41) | ✅ | 解密日志提取全部参数 |
| TryExtractGachaUrl_PlaintextLog_AlsoExtracted (:60) | ✅ | 明文日志兼容 |
| TryExtractGachaUrl_NoUrl_ReturnsNull (:75) | ✅ | 无 URL/无目录 null |
| TryExtractGachaUrl_LogInWinePrefix_FoundViaPrefixCandidates (:85) | ✅ | Proton prefix 日志候选（不猜用户名/项目名） |
| TryExtractGachaUrl_InstallDirLogPreferredOverPrefix (:105) | ✅ | 安装目录优先 |
| FetchPoolAsync_BuildsRequestBodyAndStopsOnShortPage (:125) | ✅ | 官方六字段请求体 + 无鉴权头 + 短页停 + 无游标 |
| FetchPoolAsync_InternationalDomain_UsesNet (:155) | ✅ | 国际服 .net 域名 |
| MergeAndSave_DeduplicatesAndSortsByTime (:168) | ✅ | 四元组去重 + 时间倒序 |
| LoadCached_MissingFile_ReturnsEmpty (:191) | ✅ | 无缓存空集 |
