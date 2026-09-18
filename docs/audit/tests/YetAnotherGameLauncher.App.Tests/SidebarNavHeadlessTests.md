# SidebarNavHeadlessTests 审计

- 方法数：11；判定：⚠️ 10 / ✅ 1
- 命中机制：D2（断言在 Dispatch(Action) 内——Probe1 实锤可传播，全部当前有效）
- 亮点备案：:317 CustomTitleBar 是「按 ScreenFromWindow 工作区定尺寸」的正确先例（G6 反例的对照）；
  :282 TransferCues 纯逻辑测试无 Dispatch，干净。

| 方法 | 判定 | 证据 | 修复方案 | 守卫行为 |
|---|---|---|---|---|
| SettingsPage_ClearsGameListSelection_AndHighlightsSettingsNav (:28) | ⚠️ D2 | :38-51 断言在内 | 断言外移 | 换设置页清空列表选中（TwoWay 转发链）+ 导航高亮互斥 |
| AboutPage_HighlightsAboutNavOnly (:57) | ⚠️ D2 | :70-72 | 同上 | 关于页高亮唯一性 |
| GameSettingsPage_KeepsGameSelectedInSidebar (:78) | ⚠️ D2 | :89 | 同上 | 游戏设置页保留列表选中 |
| ClickingCurrentGameRow_FromSettings_NavigatesBackToGame (:95) | ⚠️ D2 | :111-113 真实指针点击 + :116-117 断言在内 | 断言外移（点击留在内） | 真实点击列表行走转发属性导航回详情页 |
| PageTransition_DirectionClassesFlip_AndAnimationSettles (:123) | ⚠️ D2 | :133-145 | 同上 | 页面切换方向类翻转（驱动动画的状态） |
| NavIndicator_PlacesAtSelectedGameRow_AndFollowsSelection (:151) | ⚠️ D2 | :169-181 | 同上 | 指示点几何落位与跟随（含渲染矩阵断言 helper :362-368） |
| NavIndicator_FollowsSettingsAndAboutEntries (:187) | ⚠️ D2 | :206-214 | 同上 | 指示点跟随底部导航 |
| NavIndicator_RecomputesWhenSidebarCollapses (:220) | ⚠️ D2 | :242-247 | 同上 | 收起侧栏指示点重算 + 收起态贴左缘例外 |
| NavIndicator_RapidSwitches_SettleOnFinalTarget (:253) | ⚠️ D2 | :275-276 | 同上 | 快速连点终态正确 |
| TransferCues_TwoPhase_GrowOnOldItem_Hop_RetractOnNewItem (:282) | ✅ | :293-313 纯函数 BuildTransferCues 的关键帧断言，无 Dispatch | 无 | 转场提示两段式编舞几何（渲染区间不连成一条） |
| CustomTitleBar_ButtonsPresent_AndMaximizeIconToggles (:317) | ⚠️ D2 | :333-352 断言在内；:327-329 工作区定尺寸正确 | 断言外移 | 自绘标题栏按钮存在 + 最大化/还原图标与圆角切换（真 WindowState 驱动视觉最大化链路） |
