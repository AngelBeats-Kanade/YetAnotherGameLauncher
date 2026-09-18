# GameCatalogServiceTests 审计

- 方法数：14（13 Fact + 1 Theory×5）；判定：✅ 14
- 形态：TempDir 真实文件、reloader 重载验证（非原文断言）、Theory 案例互异。

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| LoadAsync_MissingFile_ThrowsFileNotFoundException (:34) | ✅ | 缺文件抛 FileNotFound |
| LoadAsync_ValidFile_PopulatesCatalog (:42) | ✅ | 合法文件加载字段 |
| LoadAsync_InvalidContent_ThrowsValidationExceptionWithErrors (:56) | ✅ | 校验失败带错误集且 Catalog 置 null |
| LoadAsync_ValidatesUmuIdFormat (:73, T×5) | ✅ | umuId 格式校验（合法 slug/AppId 形式 vs 缺前缀/空后缀/非法字符） |
| SaveThenLoad_RoundTrips (:108) | ✅ | 保存-重载往返 |
| SaveAsync_WritesConfigFile (:124) | ✅ | 落盘存在性 |
| SaveAsync_WithoutCatalog_ThrowsInvalidOperationException (:137) | ✅ | 无目录保存抛异常 |
| GetConfigFilePath_EndsWithYaglGamesJson (:145) | ✅ | 配置路径契约 |
| GetConfigDirectory_IsUnderUserConfigRoot (:153) | ✅ | 配置目录在用户配置根下 |
| CreateDefaultFileAsync_MissingFile_WritesValidMinimalDefault (:180) | ✅ | 首运最小默认可通过全量校验 |
| CreateDefaultFileAsync_ExistingFile_IsLeftUntouched (:197) | ✅ | 已有文件不覆盖 |
| CreateDefaultFileAsync_WithValidTemplate_WritesTemplateContent (:213) | ✅ | 模板物化 |
| CreateDefaultFileAsync_InvalidTemplate_FallsBackToMinimalDefault (:228) | ✅ | 坏模板回退内置默认（防坏模板锁死首运） |
| CreateDefaultFileAsync_CreatesMissingParentDirectories (:245) | ✅ | 缺失父目录自动创建 |
