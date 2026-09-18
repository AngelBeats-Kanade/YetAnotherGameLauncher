# GameDetailPresentationTests 审计

- 方法数：4；判定：✅ 4
- 主题：详情页版本 chip 分段富文本数据面（方案 A「沉浸影院」）。

| 方法 | 判定 | 证据 | 修复方案 | 守卫行为 |
|---|---|---|---|---|
| VersionChip_LanguageSwitch_RelocalizesSegments (:23) | ✅ | :30-34 语言热切换 + Refresh 后前导重算（会话缓存版本值不变、文案必须重建） | 无 | chip 前导随语言重建 |
| VersionChip_NotInstalled_ShowsLatestVersionOnly (:38) | ✅ | :44-47 只有"最新版本 x"，Mid/Target 为空串 | 无 | 未安装态 chip 分段 |
| VersionChip_InstalledUpToDate_ShowsLocalVersionOnly (:51) | ✅ | :53-68 预写本地 state + 同版本远端 → "本地 x" | 无 | 已安装最新态 chip 分段 |
| VersionChip_HasUpdate_ShowsMigrationPair (:72) | ✅ | :76-92 预写 3.6.0 + 远端 3.8.0 → "本地 3.6.0 → 最新 3.8.0" + 状态文案 | 无 | 更新迁移对分段与状态 |
