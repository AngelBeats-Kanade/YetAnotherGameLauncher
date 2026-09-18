# GameCatalogJsonTests 审计

- 方法数：9；判定：✅ 9
- 形态：Parse/Serialize 契约测试，断模型值（:123 的原文 Contains 是对**序列化器自身输出格式**的契约断言，
  属测试目的本身，与"对落盘用户文件做原文断言"的 D4 有本质区别）。

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| Deserialize_MinimalJson_AppliesDefaults (:40) | ✅ | 缺省字段默认值（System 主题、{exe}/{installDir} 模板） |
| Deserialize_FullJson_ParsesAllFields (:50) | ✅ | 全字段解析（含 options 字典） |
| Deserialize_AcceptsPascalCase (:74) | ✅ | PascalCase 兼容 |
| Deserialize_IgnoresUnknownFields (:82) | ✅ | 未知字段前向兼容 |
| Deserialize_AcceptsCommentsAndTrailingCommas (:92) | ✅ | 注释与尾逗号（用户手改友好契约） |
| Serialize_RoundTrip_PreservesValues (:109) | ✅ | 序列化往返保真 |
| Serialize_UsesCamelCase (:123) | ✅ | 输出 camelCase 契约 |
| Deserialize_MalformedJson_ThrowsValidationException (:139) | ✅ | 坏 JSON 归一为 ValidationException |
| Deserialize_InvalidEnumValue_ThrowsValidationException (:145) | ✅ | 非法枚举归一为 ValidationException |
