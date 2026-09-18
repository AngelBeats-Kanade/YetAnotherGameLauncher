# NativeUmuCoreTests 审计

- 方法数：13；判定：✅ 13
- 形态：TempDir + 纯逻辑直调；VDF/Runtime 目录/清单/env/prefix 全链路形状断言。

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| VdfMiniParser_ParsesToolManifestFields (:15) | ✅ | VDF 四字段解析 |
| SteamRuntimeCatalog_MapsAppIdAndArchiveName (:34) | ✅ | sniper/steamrt4/arm64 后缀命名 + 未知 AppId null |
| ToolManifest_Load_BuildsEntryCommandWithVerb (:57) | ✅ | toolmanifest + compatibilitytool 双文件加载、require_tool_appid → steamrt4、入口命令 |
| UmuEnvironment_Build_SetsCompatKeysAndAppId (:95) | ✅ | GAMEID/UMU_ID/WINEPREFIX=STEAM_COMPAT_DATA_PATH/PROTONPATH/EXE 全键 |
| UmuEnvironment_Build_UmuIdOverrideWinsAndAppIdStaysMd5 (:125) | ✅ | umuId 覆盖生效且 AppId 恒为 prefix MD5（上游语义） |
| UmuPrefix_Setup_CreatesLayout (:153) | ✅ | prefix 布局（shadercache/gstreamer/tracked_files/pfx 链接或目录） |
| NativeUmuLauncher_BuildEntryCommand_WrapsWithRuntimeEntryPoint (:167) | ✅ | _v2-entry-point 包裹 + --verb + -- + proton run exe 参数序 |
| ResolveNativeProtonRequest_UsesEnvThenDefaultFlavor (:195) | ✅ | PROTONPATH 优先、空/缺省回 DW-Proton 绝不 UMU-Proton（AGENTS.md 契约） |
| BuildNativeUmuLaunch_UsesTokenTemplate (:211) | ✅ | native-umu 模板 + 默认 DW 代号 |
| BuildNativeUmuLaunch_UmuIdAndFlavorOverride (:221) | ✅ | umuId 与发行版覆盖 |
| BuildRecommendedLaunch_PrefersNativeUmu (:231) | ✅ | 原生 umu 优先 |
| BuildRecommendedLaunch_PassesUmuIdThrough (:243) | ✅ | umuId 透传 + prefix 按游戏 id 不迁移 |
| BuildRecommendedLaunch_NativeDisabled_NoProtonInstalled_FallsBackToNativeTemplate (:257) | ✅ | 原生链禁用且无 Proton/wine 仍落 native-umu 模板 |
