# MainWindowViewModelTests.cs 审计（含同文件三个测试类）

- 方法数：29（MainWindowViewModelTests 16 + SidebarNavigationTests 8 + ConfigMigrationTests 5）；判定：✅ 29
- 形态：全部纯 VM 直调（无 Dispatch）、真实断言、Fake 渠道注入确定性强、落盘断言走
  GameCatalogService 重载/字节对比（非原文 Contains）。正面样板。

## MainWindowViewModelTests（16 ✅）

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| PersistWindowState_WritesSettingsAndSurvivesReload (:21) | ✅ | 窗口状态持久化 + 跨启动重载往返 |
| PersistWindowState_FreshConfig_NoPersistedSize (:45) | ✅ | 首运无持久化尺寸 → null（用 XAML 默认） |
| Initialize_LoadsTwoGamesAndSelectsFirst (:58) | ✅ | 初始化加载与默认选中 |
| Initialize_ThemeFromConfig_IsApplied (:68) | ✅ | 配置主题反映到 VM |
| Initialize_GameStatus_ReflectsChannelInfo (:77) | ✅ | 未安装态全字段（含"未安装谈不上更新"） |
| SelectingGame_SwitchesPageAndRefreshes (:98) | ✅ | 选中切页 + chip 数据来自预热缓存（同步可得，:103 Yield 无时序风险） |
| InstallOrUpdate_RunsUpdateThroughFakeChannel_SavesState (:110) | ✅ | 安装走渠道清单 + 状态行保留真实状态（不被"完成"覆盖） |
| Predownload_WhenServerOpensWindow_StagesAndShowsBadge (:132) | ✅ | 预下载暂存 + 徽章态 + 按钮隐藏 |
| LaunchCommand_WithoutInstall_DoesNothing (:159) | ✅ | 未安装启动是 no-op |
| UnknownChannel_SkippedWithMessage (:170) | ✅ | 未注册渠道跳过 + 提示 |
| Initialize_MissingConfigFile_WithTemplate_GeneratesDefaultAndLoadsGames (:183) | ✅ | 首运模板物化（重载解析强断言） |
| Initialize_MissingConfigFile_WithoutTemplate_GeneratesMinimalDefault (:203) | ✅ | 无模板 → 最小合法配置 |
| Initialize_SecondRun_DoesNotRegenerate (:218) | ✅ | 二次启动字节级不覆盖 |
| Initialize_LanguageFromConfig_IsApplied (:235) | ✅ | 语言从配置生效（含 1 games 复数文案、主题显示名） |
| SaveLanguage_WritesBackToConfigFile (:261) | ✅ | 语言写盘往返 |
| LanguageSwitch_RefreshesExistingGameTexts (:274) | ✅ | 热切换后显式等待刷新完成再断言 |

## SidebarNavigationTests（8 ✅）

高亮互斥（:299/:312/:401）、返回方向标记（:325/:339/:353/:367）、宽度滞回自动收展
（:381，含手动收起后宽窗口以宽度为准）——全部直调、行为级断言。

## ConfigMigrationTests（5 ✅）

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| Initialize_OldSchema_MergesSampleServersAndLocalizedNames (:455) | ✅ | 旧配置模板合并服务器/本地化名 + 迁移幂等（字节相等） |
| UpdateInstallRoot_PersistsAndRebuildsGames (:474) | ✅ | 安装根目录持久化 + 列表重建（分隔符两侧归一） |
| UpdateInstallRoot_Empty_ReturnsFalse (:490) | ✅ | 空根目录拒绝 |
| BrowseInstallRoot_PicksFolder_NormalizesAndSaves (:498) | ✅ | 选夹反斜杠规范化 + 直接落盘 + 起始位置 |
| BrowseInstallRoot_Cancel_LeavesDraftAndConfigUntouched (:522) | ✅ | 取消不动草稿与配置 |
