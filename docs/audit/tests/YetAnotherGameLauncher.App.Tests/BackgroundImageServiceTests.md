# BackgroundImageServiceTests 审计

- 方法数：10；判定：✅ 1 / ❌ 9
- 命中机制：D1（`Dispatch(async ...)` 全形态吞断言，Probe2/3 实锤）

| 方法 | 判定 | 证据 | 修复方案 | 守卫行为 |
|---|---|---|---|---|
| LoadAsync_LocalFile_ReturnsImage (:16) | ❌ D1 | :20 `Dispatch(async () =>`，:24 `await File.WriteAllBytesAsync`，:29 `Assert.NotNull` 在 lambda 内 | 会话初始化后测试线程直调 `LoadAsync`（BackgroundResilienceTests 形态）；TempDir 生命周期随测试方法 | 本地文件 → Bitmap 解码返回非空 |
| LoadAsync_MissingLocalFile_ReturnsNull (:33) | ❌ D1 | :36 Dispatch(async)，:43 `Assert.Null` 在内 | 同上 | 文件不存在 → null（不抛） |
| LoadAsync_HttpSource_ReturnsImageAndCaches (:47) | ❌ D1 | :56-57 双 await，:59-60 断言在内（含 `Assert.Same` 内存缓存契约） | 同上 | http 加载 + 内存缓存同实例复用 |
| LoadAsync_Http_SecondInstance_ServedFromDiskCache (:64) | ❌ D1 | :74/:78 await，:80-81 断言在内 | 同上 | 新实例命中磁盘缓存、零网络（Requests 恰 1） |
| ReloadAsync_BypassesCaches_RefetchesAndRewritesDisk (:85) | ❌ D1 | :95/:97 await，:100-102 断言在内 | 同上 | Reload 绕过两级缓存并回写磁盘 |
| LoadAsync_Http_PoisonedNetworkBytes_SelfHealsViaNetwork (:106) | ❌ D1 | :118 `Assert.Null(await ...)` 即 lambda 内断言 | 同上 | 非图片字节不入缓存；恢复后重下成功 |
| LoadAsync_Http_CorruptCacheFile_DeletedAndRefetched (:127) | ❌ D1 | :145-150 断言全在内 | 同上 | 坏缓存文件被删除；命中磁盘不走网络；回落网络成功 |
| LoadAsync_Http_CleansStaleTempsOnWrite (:154) | ❌ D1 | :171-172 断言在内 | 同上 | 写前清理遗留 .download-stale 临时文件 |
| LoadAsync_HttpFailure_ReturnsNull (:177) | ❌ D1 | :184 await，:186 断言在内 | 同上 | 网络失败 → null（瞬时失败缓存 TTL） |
| LoadAsync_EmptySource_ReturnsNull (:190) | ✅ | :193 测试线程直调，:195-196 断言在方法体 | 无 | 空/null 源短路返回 null |

备注：9 个 ❌ 的 `using var dir`（TempDir）在 lambda 内声明——Dispatch 提前返回后，
TempDir.Dispose 与孤儿续体竞态（AGENTS.md 12.1.2 实锤"首个 await 即提前返回"），
修复时必须把资源生命周期还给测试方法体。
