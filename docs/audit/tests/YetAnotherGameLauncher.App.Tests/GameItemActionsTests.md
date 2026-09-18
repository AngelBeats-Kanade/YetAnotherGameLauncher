# GameItemActionsTests 审计

- 方法数：8；判定：✅ 7 / ⚠️ 1
- 主题：GameItemViewModel 操作语义（抽卡门控/预下载提示/校验修复文件式与包式/既有安装检测/主程序选择/代理持久化/登记版本）。

| 方法 | 判定 | 证据 | 修复方案 | 守卫行为 |
|---|---|---|---|---|
| GachaEntry_GatedOnInstallState (:51) | ✅ | :56-71 未安装无入口→安装后有→非鸣潮渠道永无 | 无 | 抽卡入口 = 鸣潮渠道 且 已安装 |
| PredownloadCue_ReflectsInstallAndUpdateState (:75) | ✅ | :79-99 未安装不提示→装后提示→新版出现转更新优先；internal ResetVersionCheckCache 模拟重启 | 无 | 预下载提示状态机与更新优先级 |
| Verify_OnFileChannel_ReportsRepairedCount (:103) | ✅ | :111-120 同尺寸损坏字节（快速校验盲区）→修复计数 + 字节级还原断言 | 无 | MD5 事后校验补下载修复 |
| DetectExistingInstall_AllowsDirectLaunch (:124) | ✅ | :129-143 未登记不可启动→文件在则可直启 + 按钮文案变化 | 无 | 既有安装检测状态机 |
| ExecutableDraft_PickedPathSavedRelativeAndDetected (:147) | ⚠️ D4 | :166 已断模型值（强）；:170-171 补了一个落盘原文 Contains（弱，转义可骗过） | :170-171 改反序列化断 savedGame.Executable | 主程序相对路径写回配置 + 即时可启动 |
| ProxyRadios_MapToModesAndPersist (:175) | ✅ | :196-210 经 JsonDocument.Parse 断值（非原文 Contains）+ radio 互斥语义 | 无 | 代理三态互斥与持久化（Manual/None/地址清空/无效地址失败不落盘） |
| RegisterVersion_OnPackageChannelDetected_RegistersWithoutDownload (:231) | ✅ | :246-252 下载计数零增量 + 状态一致 | 无 | 包式渠道"登记版本"零下载 |
| Verify_OnPackageChannel_ConfirmThenReinstall (:257) | ✅ | :262-279 首装不走确认→校验弹确认→取消零下载→确认整包重下 | 无 | 包式渠道校验修复确认流 |
