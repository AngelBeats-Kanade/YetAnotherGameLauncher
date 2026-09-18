# LaunchSettingsTests 审计

- 方法数：10；判定：✅ 10
- 形态：直调 + GameCatalogService 重载往返断言（非原文 Contains）+ 有界条件轮询（合规形态）。

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| Save_UpdatesGameLaunchAndConfigFile (:18) | ✅ | 模板/工作目录/环境变量保存往返（逐键断言） |
| Save_InvalidEnvironmentLine_ShowsErrorWithoutSaving (:40) | ✅ | 坏 env 行：页内红字、不弹 toast、不落盘 |
| Save_EmptyCommandTemplate_ShowsError (:58) | ✅ | 空模板校验失败 |
| InitialValues_ComeFromGameLaunch (:71) | ✅ | 草稿初值来自配置 |
| BrowseInstallDir_PicksFolder_NormalizesAndSaves (:82) | ✅ | 选夹规范化 + 触发整卡保存 + 起始位置=原草稿 |
| BrowseInstallDir_Cancel_LeavesDraftUntouched (:104) | ✅ | 取消不保存不提示 |
| Save_WithActualChange_RaisesToastListingChangedField (:118) | ✅ | 实际变更弹轻提示并列出变更字段 |
| Save_WithMultipleChanges_JoinsLabelsInFixedOrder (:135) | ✅ | 多字段标签固定顺序连接（锁键缺失回退键名） |
| Save_WithoutChanges_DoesNotRaiseToast (:151) | ✅ | 无变更不弹 toast（页内消息照常） |
| ProtonFlavorSwitch_ImmediateSave_RaisesFlavorToast (:165) | ✅ | 发行版即切即存且提示按"Proton 发行版"汇报（:179-182 有界条件轮询 = D5 合规形态） |
