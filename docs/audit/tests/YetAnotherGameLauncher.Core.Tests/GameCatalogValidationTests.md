# GameCatalogValidationTests 审计

- 方法数：17；判定：✅ 17
- 形态：纯校验函数直调，逐字段错误路径 + 错误累积 + 大小写不敏感重复 id。

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| Validate_ValidCatalog_ReturnsNoErrors (:31) | ✅ | 合法目录零错误 |
| Validate_MissingGameId_ReportsError (:39) | ✅ | 空 id |
| Validate_WhitespaceGameId_ReportsError (:47) | ✅ | 空白 id |
| Validate_GameIdWithPathSeparator_ReportsError (:55) | ✅ | 路径分隔符 id（防目录逃逸） |
| Validate_DuplicateGameIds_ReportsError (:63) | ✅ | 重复 id |
| Validate_DuplicateGameIdsCaseInsensitive_ReportsError (:71) | ✅ | 大小写不敏感重复 |
| Validate_MissingDisplayName_ReportsError (:79) | ✅ | 空显示名 |
| Validate_MissingChannel_ReportsError (:87) | ✅ | 空渠道 |
| Validate_MissingInstallDir_ReportsError (:95) | ✅ | 空安装目录 |
| Validate_MissingExecutable_ReportsError (:103) | ✅ | 空主程序 |
| Validate_NoServers_ReportsError (:111) | ✅ | 无服务器 |
| Validate_DuplicateServerIds_ReportsError (:119) | ✅ | 重复服务器 id |
| Validate_MissingServerId_ReportsError (:127) | ✅ | 空服务器 id |
| Validate_MissingServerName_ReportsError (:135) | ✅ | 空服务器名 |
| Validate_EmptyCommandTemplate_ReportsError (:143) | ✅ | 空命令模板 |
| Validate_InstallRootMissing_ReportsError (:151) | ✅ | 空安装根 |
| Validate_AccumulatesErrorsFromMultipleGames (:159) | ✅ | 多游戏错误累积（≥2） |
