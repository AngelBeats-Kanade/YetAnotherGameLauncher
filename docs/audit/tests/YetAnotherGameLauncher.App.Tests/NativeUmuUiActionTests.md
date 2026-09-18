# NativeUmuUiActionTests 审计

- 方法数：10；判定：✅ 10
- 形态：直调 + 可编程 FakeProvisioner（记录调用参数），行为级断言。

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| LaunchSettings_NativeMode_ShowsMissingStatusAndPreparesComponents (:104) | ✅ | 组件缺失态文案 + 一键准备调用 EnsureProton/EnsureRuntime 各一次并转"就绪" |
| LaunchSettings_RuntimeResolvedFromManifest_DrivesStatusAndPrepare (:129) | ✅ | 老 Proton manifest 声明的 steamrt3 驱动状态询问与下载（不用默认 steamrt4） |
| LaunchSettings_CheckProtonUpdate_NewVersion_ShowsConfirmAndButtonBecomesUpdate (:157) | ✅ | 新版检测 → 确认覆盖层 + 按钮变"更新到 {tag}" + WORD JOINER 防拆行同构断言 + 取消保持更新态 |
| LaunchSettings_ConfirmProtonUpdate_UpdatesAndResetsButton (:189) | ✅ | 确认更新调用 Update、状态机复位 Idle、消息含新版号 |
| LaunchSettings_CheckProtonUpdate_UpToDate_NoDialog (:214) | ✅ | 已最新不弹层、状态 Idle、消息含版本 |
| LaunchSettings_FlavorSelection_PersistsProtonPathImmediately (:234) | ✅ | 发行版即切即存（草稿/模型同步，防启动链错位） |
| LaunchSettings_LanguageSwitch_RefreshesCheckButtonText (:249) | ✅ | 计算属性文案随 Item[] 通知重建（en/zh 双向） |
| LaunchError_DownloadKind_ExposesRetry (:271) | ✅ | 下载类失败暴露重试动作（事件触发） |
| LaunchError_ProtonDownload_ExposesLocalProtonPicker (:288) | ✅ | 本地 Proton 选择器透传所选值 |
| LaunchError_NoLocalProtons_HidesPicker (:306) | ✅ | 无本地 Proton 隐藏选择器 |
