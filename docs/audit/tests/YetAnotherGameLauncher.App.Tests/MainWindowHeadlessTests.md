# MainWindowHeadlessTests 审计

- 方法数：6；判定：⚠️ 6（全文件 D2：断言在 Dispatch(Action) 内——Probe1 实锤可传播、当前全部有效）

| 方法 | 判定 | 证据 | 修复方案 | 守卫行为 |
|---|---|---|---|---|
| MainWindow_ShowsTwoGamesFromSampleConfig (:20) | ⚠️ D2 | :30-31 断言在内 | 断言外移 | XAML 构建 + 列表渲染两条目 |
| MainWindow_GameDetailShowsSelectedGameName (:37) | ⚠️ D2 | :47-48 | 同上 | 详情页渲染游戏名与状态文案 |
| MainWindow_SettingsPageSwitchesContent (:54) | ⚠️ D2 | :62 VM 命令驱页 + :66-69 断言在内 | 同上 | 设置页内容真实渲染（外观/主题/语言） |
| MainWindow_AboutPageSwitchesContent (:75) | ⚠️ D2 | :83/:87-89 | 同上 | 关于页内容真实渲染 |
| MainWindow_SidebarToggle_CollapsesWidthAndTexts (:95) | ⚠️ D2 | :111-118 | 同上 | 侧栏收起：宽度 68 + 文本 IsEffectivelyVisible=false（真实布局断言） |
| MainWindow_LanguageSwitch_UpdatesRenderedTexts (:124) | ⚠️ D2 | :137 | 同上 | 语言热切换经绑定刷新到渲染文本 |
