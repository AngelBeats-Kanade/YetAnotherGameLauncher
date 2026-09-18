# LaunchSettingsPlatformTests 审计

- 方法数：5（4 Fact + 1 Theory×5 案例）；判定：✅ 5
- 形态：直调、FakePlatformInfo 双平台确定注入、行为级断言。

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| LinuxMode_RecommendsNativeUmuWithSteamOsAndNvapi (:20) | ✅ | Linux 模式清单顺序（NativeUmu,Direct）、推荐态、SteamOS/NVAPI env、DW-Proton 缺省代号 |
| WindowsMode_StaysDirectWithoutCompatEnv (:43) | ✅ | Windows 保持直启、无兼容 env |
| LinuxLegacyTemplate_ShowsUmuModeWithoutRewritingDraft (:60) | ✅ | 存量模板仅作显示映射，草稿不被重写 |
| ProtonFlavorChange_RewritesProtonPathCodename (:79) | ✅ | 发行版切换改写 PROTONPATH 代号（旧代号清除） |
| ProtonFlavor_DetectedFromExistingProtonPath (:100, T×5) | ✅ | 已存 PROTONPATH → 发行版检测（绝对路径 dw/GE 名/UMU-Proton/嵌套 GE/空→缺省 DW），数据互异有意义 |
