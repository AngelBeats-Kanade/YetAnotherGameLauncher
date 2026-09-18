# LinuxFirstRunLaunchTests 审计

- 方法数：10；判定：⚠️ 10（全文件 D4）
- 运行形态本身正确：测试线程直调 `InitializeAsync`，无 Dispatch，断言可传播。
- 共性问题（D4）：对落盘 JSON 的**原始文本**做 Contains/Regex，而非反序列化断模型值。
  风险 1（假红）：锁死序列化器输出格式（`"schemaVersion": 5` 冒号后空格、键序、缩进）——
  序列化选项一变即红；风险 2（假绿空间）：Contains 单点匹配（如 :27 `Contains("native-umu")`）
  允许该串出现在无关字段而模板实际未升级。修复 = 读盘后 `JsonSerializer.Deserialize<AppSettings>`
  断 `CommandTemplate == "native-umu {exe}"`、环境变量字典、`SchemaVersion == 5` 等模型值。

| 方法 | 判定 | 证据 | 修复方案 | 守卫行为 |
|---|---|---|---|---|
| FirstRun_Linux_UpgradesBareDirectTemplateToNativeUmu (:16) | ⚠️ D4 | :27-31 全部原文 Contains + 正则计数 | 反序列化断模型值 | Linux 首运：裸 {exe} 模板升级 native-umu + SteamOS 伪装 + NVIDIA env + DW-Proton 代号 |
| FirstRun_Linux_WithoutProtonOrUmu_FallsBackToNativeUmuTemplate (:35) | ⚠️ D4 | :47-49 原文 | 同上 | 无任何运行时仍给 native-umu（启动时自动准备组件） |
| FirstRun_Linux_WithoutUmuAndProton_ButWithWine_StillUsesNativeUmuDefault (:53) | ⚠️ D4 | :65-66 原文 | 同上 | 有 wine 也不退回 wine 直启（统一 umu 链） |
| FirstRun_Linux_CustomTemplate_LeftUntouched (:70) | ⚠️ D4 | :82-84 原文 DoesNotContain | 反序列化断 CommandTemplate 原值 | 用户自定义模板不被首运覆盖 |
| FirstRun_Windows_KeepsBareDirectTemplate (:88) | ⚠️ D4 | :98-100 原文 | 同上 | Windows 首运保持 {exe} 原样 |
| Migration_Linux_OldBareTemplate_UpgradedToRecommendedChain (:106) | ⚠️ D4 | :117-118 含 `"schemaVersion": 5` 格式锁 | 同上 | 存量 schema<4 裸模板一次性升级 |
| Migration_Linux_CustomTemplate_LeftUntouched_ButVersionBumped (:122) | ⚠️ D4 | :132-134 格式锁 | 同上 | 自定义模板不动但 schemaVersion 升 5 |
| Migration_Linux_LegacyUmuTemplate_UpgradedToNativeUmu (:138) | ⚠️ D4 | :150-155 含键值对字面量锁（GAMEID/PROTONPATH） | 同上 | 存量外部 umu-run 模板升级为 native-umu 并保留既有 env、补缺省 PROTONPATH |
| Migration_Schema4_NeverTouchesAgain (:159) | ⚠️ D4 | :169-170 原文 | 同上 | schema=4 裸模板为既成事实不再迁移 |
| Migration_Schema5_NeverTouchesAgain (:174) | ⚠️ D4 | :185-187 原文 | 同上 | schema=5 已迁移配置永不再改写 |
