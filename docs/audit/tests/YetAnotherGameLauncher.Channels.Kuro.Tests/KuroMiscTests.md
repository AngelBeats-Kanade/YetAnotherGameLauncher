# KuroBackdropResolverTests / KuroServiceCollectionExtensionsTests 审计

## KuroBackdropResolverTests（3 ✅）

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| GetBackdropUrlAsync_RemoteTwoHop_ReturnsVideoWithPoster (:36) | ✅ | 视频投放 + 首帧海报 |
| GetBackdropUrlAsync_RemoteStaticFileType_ReturnsImage (:56) | ✅ | 静态图投放不带海报 |
| GetBackdropUrlAsync_NothingAvailable_ReturnsNull (:76) | ✅ | 无配置 null |

## KuroServiceCollectionExtensionsTests（2 ✅）

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| AddKuroChannel_RegistersKeyedApi (:12) | ✅ | 渠道键 DI 注册 |
| GetPredownloadManifestAsync_DefaultImplementation_ReturnsNull (:38) | ✅ | 接口默认实现（文件式渠道无包式预下载） |
