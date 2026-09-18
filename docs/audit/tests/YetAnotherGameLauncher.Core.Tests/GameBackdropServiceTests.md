# GameBackdropServiceTests 审计

- 方法数：16；判定：✅ 16
- 形态：直调 + StubResolver（记录调用次数）+ StubHttpHandler；:59 用"同名目录占位"跨平台复现
  Windows 文件占用语义（AGENTS.md 跨平台防线的正面测试样板）。

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| Resolve_RemoteUrl_DownloadsAndCaches (:23) | ✅ | 下载落盘字节精确 + 二次命中缓存（零重复下载） |
| Resolve_UrlChanged_RetractsNewBackdrop (:41) | ✅ | 固定路径覆盖式缓存 + 内容替换 |
| Resolve_OldBackdropUndeletable_FallsBackToTimestampedName (:59) | ✅ | 旧文件删不掉 → 时间戳备用名落盘（GameBackdropService 换名防线） |
| Resolve_VideoSource_DownloadsBackdropAndPoster (:83) | ✅ | 视频本体+海报双落盘、扩展名正确 |
| Resolve_VideoCached_ReuseWithoutRedownload (:101) | ✅ | 视频缓存复用（恰 2 次请求） |
| Resolve_KindChanged_RetractsNewBackdrop (:118) | ✅ | 同 URL 图变视频也重下 |
| Resolve_ResolverFails_FallsBackToCachedFile (:133) | ✅ | 解析失败回退已缓存文件（离线韧性） |
| Resolve_UnknownChannel_ReturnsNull (:148) | ✅ | 未注册渠道 null |
| Resolve_VersionUnchanged_SkipsResolverAndNetwork (:159) | ✅ | 版本门控：零解析零下载 |
| Resolve_VersionChanged_ReinvokesResolver_ButDoesNotRedownloadSameUrl (:175) | ✅ | 版本变化重解析但同地址不重下 + 元数据升级 |
| Resolve_RegionChanged_ReinvokesResolver_EvenIfVersionSame (:192) | ✅ | 区域变化即使版本同也重解析 |
| Resolve_LegacyMetaWithoutVersion_InvokesResolverOnceThenGates (:209) | ✅ | 旧格式缓存元数据升级一次后可门控 |
| ResolveCached_Hit_ReturnsFile_WithoutResolverOrNetwork (:231) | ✅ | 纯缓存查询零解析零网络 |
| Resolve_PosterDownloadFails_VideoCacheStillLands (:250) | ✅ | 海报 404 不作废本体缓存 |
| ResolveCached_Miss_ReturnsNull (:266) | ✅ | 缓存未命中 null |
| GetCachedGameVersion_NoCache_ReturnsNull (:275) | ✅ | 无缓存版本 null |
