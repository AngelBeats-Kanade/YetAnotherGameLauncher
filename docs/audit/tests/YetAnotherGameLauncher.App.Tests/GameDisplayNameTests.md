# GameDisplayNameTests 审计

- 方法数：3（1 Theory×2 + 2 Fact）；判定：✅ 3

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| DisplayName_UsesLocalizedMapForCurrentLanguage (:34, T×2) | ✅ | nameLocalized 按当前语言取值（zh/en 双案例）+ IconText 取首字符 |
| DisplayName_MissingMap_FallsBackToDisplayName (:48) | ✅ | 缺映射回退 displayName |
| Migration_FillsLocalizedNamesFromSample (:58) | ✅ | 旧配置迁移从样例模板补齐名称映射（断模型字典值） |
