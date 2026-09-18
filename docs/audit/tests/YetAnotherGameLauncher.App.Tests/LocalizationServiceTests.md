# LocalizationServiceTests 审计

- 方法数：9；判定：✅ 9
- 形态：直调、确定性强；:31 用 try/finally 保护 CurrentUICulture 全局状态（正确）。

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| Default_ReturnsChinese (:11) | ✅ | 默认 zh-CN 与键值 |
| SetLanguage_English_ReturnsEnglish (:20) | ✅ | en-US 切换 |
| SetLanguage_System_ResolvesCurrentUICulture (:31) | ✅ | System 档按 CurrentUICulture 解析（双向验证 + 全局状态还原） |
| UnknownKey_ReturnsKeyItself (:53) | ✅ | 缺键回退键名本身 |
| UnknownLanguage_FallsBackToDefault (:61) | ✅ | 未知语言回退默认 |
| Format_NoArgs_KeepsCurlyPlaceholders (:72) | ✅ | {exe} 占位符不被 string.Format 吃掉（无参原样返回） |
| Format_WithArgs_Interpolates (:81) | ✅ | 有参插值 |
| SetLanguage_RaisesItemNotification (:89) | ✅ | Item[] 通知（绑定刷新依赖） |
| LanguageResources_HaveParity (:101) | ✅ | 中英资源键集完全一致（防漏译守卫） |
