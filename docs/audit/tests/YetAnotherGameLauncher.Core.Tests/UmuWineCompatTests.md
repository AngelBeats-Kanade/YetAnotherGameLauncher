# UmuWineCompatTests / UpdatePlannerTests 审计

## UmuWineCompatTests（11 ✅）

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| FindSystemWine_FoundOnPath (:26) | ✅ | PATH 注入扫描 wine（.exe 补试语义在 Windows 腿同样生效） |
| FindSystemWine_Missing_ReturnsNull (:36) | ✅ | 缺失 null |
| FindLutrisWineVersions_ListsRunnerDirs (:42) | ✅ | Lutris runner 目录列举（无 bin/ 不算） |
| PrefixRoot_UnderDataHomeYagl (:57) | ✅ | prefix 根 = 数据目录 yagl/prefixes |
| PrefixPathFor_GameScoped (:65) | ✅ | 每游戏一 prefix |
| BuildProtonLaunch_PrefixLivesOutsideInstallDir (:73) | ✅ | prefix 完全脱离游戏安装目录（AGENTS.md 数据安全契约） |
| BuildWineLaunch_SetsWinePrefix (:93) | ✅ | wine 模板 + WINEPREFIX 统一位置 |
| BuildRecommendedLaunch_ProtonFallback_WhenNativeDisabled (:109) | ✅ | 推荐链：Proton 回退 |
| BuildRecommendedLaunch_WineFallback_WhenNativeDisabledAndNoProton (:124) | ✅ | 推荐链：wine 回退 |
| BuildRecommendedLaunch_NothingInstalled_StaysNativeUmuTemplate (:137) | ✅ | 推荐链：全空回退 native-umu |
| IsGeneratedEnvironmentKey_CoversCompatAndUmuKeys (:149) | ✅ | 生成 env 键分类（迁移/保留判定依赖） |

## UpdatePlannerTests + VersionComparisonTests（5 ✅ + 3 ✅，其中 Theory×6）

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| Plan_NoLocalVersion_FullSync (:10) | ✅ | 未安装全量 |
| Plan_LocalVersionMatchesPatch_Incremental (:20) | ✅ | 版本命中补丁源走增量 |
| Plan_LocalVersionNotInPatches_FullSync (:30) | ✅ | 跳版本全量 |
| Plan_MatchingIsExactStringComparison (:39) | ✅ | 精确串比较（"3.5"≠"3.5.0"） |
| Plan_EmptyPatches_FullSync (:48) | ✅ | 无补丁全量 |
| IsNewer_NumericVersions (:65, T×6) | ✅ | 数值比较含进位（3.9<3.10、1.0.0.9<1.0.1、等值 false） |
| IsNewer_NonNumericVersions_FallBackToStringComparison (:71) | ✅ | 非数值回退串比较 |
| IsNewer_NothingInstalled_ReturnsFalse (:78) | ✅ | 未安装/空串 false |
