# CompatToolsTests / ProtonCompatTests 审计

## CompatToolsTests（3 ✅）

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| FindProtonVersions_ScansKnownDirs_PutsDefaultFirst (:14) | ✅ | 已知目录扫描 + dw-proton 推荐置顶 + 非 Proton 忽略 |
| BuildProtonLaunch_GeneratesTemplateAndEnv (:28) | ✅ | Proton 模板生成 + prefix 路径（数据根显式注入不随 home 漂移） |
| BuildProtonLaunch_MissingVersion_FallsBackToExpectedPath (:44) | ✅ | 版本缺失回退期望路径 + CLIENT_INSTALL_PATH |

## ProtonCompatTests（10 ✅）

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| FindProtonVersions_ListsCompatibilityToolsDirs (:25) | ✅ | compatibilitytools.d 全收 + 推荐置顶 |
| FindProtonVersions_CommonRoot_KeepsOnlyProtonPrefixed (:36) | ✅ | common 根只留 Proton* 条目 |
| FindProtonVersions_MissingHome_ReturnsEmpty (:49) | ✅ | home 缺失空集 |
| PickRecommendedProton_GeProtonWinsOverEverything (:55) | ✅ | GE 字典序最新胜出 |
| PickRecommendedProton_Empty_ReturnsNull (:63) | ✅ | 空集 null |
| BuildProtonLaunch_QuotesProtonAndSetsCompatEnv (:69) | ✅ | 引号模板 + 双 compat env |
| LocateProton_FindsExistingVersion (:88) | ✅ | 定位存在版本 + 缺失 null |
| RecommendedEnvironment_Wuthering_PretendsSteamOs (:99) | ✅ | 鸣潮 SteamOS 伪装、其它游戏空 |
| BuildRecommendedLaunch_MergesCompatEnvAndRecommendations (:108) | ✅ | 推荐启动合并（Proton 模式 + NVAPI + SteamOS） |
| BuildRecommendedLaunch_NoVersions_FallsBackToNativeUmuTemplate (:125) | ✅ | 零运行时回退 native-umu 模板 |
