# DetailPageHeadlessTests 审计

- 方法数：6；判定：⚠️ 6（全文件 D2：断言在 Dispatch(Action) 内——Probe1 实锤可传播、当前全部有效；违纪形态，Phase 2 统一外移或以哨兵+纪律修订固化）

| 方法 | 判定 | 证据 | 修复方案 | 守卫行为 |
|---|---|---|---|---|
| GlassOnArtButton_KeepsLightForeground_InDisabledAndHoverStates (:33) | ⚠️ D2 | :47-69 断言在内；真实指针 hover（:65-67）+ presenter 层取色，守卫真实 | 断言外移 | glass-onart 按钮禁用/悬停态浅色前景（Fluent presenter 压制问题的回归） |
| ContentCard_UniformPageSheet_BelowTitleBand (:75) | ✅→⚠️ D2 | :91-110 断言在内；三次换页 + Margin/CornerRadius/高度断言 | 断言外移 | 全出血页面板统一布局与色带几何 |
| GameDetailPage_PosterImage_IsLeftAnchored (:116) | ⚠️ D2 | :128-129 断言在内 | 断言外移 | 海报左上锚定（UniformToFill 裁切回归） |
| GameDetailPage_ChipsRow_ProposalALayout (:135) | ⚠️ D2 | :152-160 断言在内 | 断言外移 | chips 簇左上落位、纱带不拦交互 |
| ActionDock_ValueColumn_KeepsLightForeground_InLightTheme (:166) | ⚠️ D2 | :184-189 断言在内（亮色主题黑字叠黑底回归） | 断言外移 | 操作坞值列显式浅色前景 |
| GameDetailPage_ChipsRow_LongStatus_WrapsInsteadOfClipping (:196) | ⚠️ D2 | :204 已正确解除 MinWidth（先例）；:221-224 断言在内 | 断言外移 | 长 status 文案换行不越界（行为级断言：版本 chip 换行 + 右缘不出页） |
