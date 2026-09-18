# BackgroundResilienceTests 审计

- 方法数：4；判定：✅ 4
- 说明：缓存策略纯逻辑，测试线程直调（无 Dispatch），ManualTimeProvider 驱动时钟，确定性。

| 方法 | 判定 | 证据 | 修复方案 | 守卫行为 |
|---|---|---|---|---|
| FailureWithinInterval_ServedFromCache (:22) | ✅ | :27 Store 失败后 :29 TTL 内 TryGetCached 返回 null | 无 | 瞬时失败短缓存：TTL 内不再打网络 |
| FailureExpires_RetryAllowed (:33) | ✅ | :38 Store，:40 TTL 过期后不再命中 | 无 | TTL 过期允许重试 |
| SuccessIsCachedPermanently_EvenWithZeroTtl (:44) | ✅ | :48 假 IImage，:51 Advance 1 天后 :53 Still Same | 无 | 成功条目不受 TTL 影响 |
| ExpiredFailure_IsRemovedOnLookup (:57) | ✅ | :62-68 过期清除 + 重写成功可命中 | 无 | 过期失败条目在查询时被移除 |
