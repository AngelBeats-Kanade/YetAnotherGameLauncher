# WindowStateMapperTests 审计

- 方法数：8（3 Fact + 4 Theory，案例含 2026-09 真机实测值）；判定：✅ 8
- 形态：纯函数决策表直调（DIP/物理双单位候选、±4px 容差、缩放非法回退）。

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| NonMaximizedStates_AreNeverVisuallyMaximized (:21, T×3) | ✅ | Normal/Minimized/FullScreen 永非视觉最大化 |
| Maximized_WithoutScreenInfo_FallsBackToPropertySemantics (:29) | ✅ | 无屏幕信息退回属性语义（headless/X11 既有行为不变） |
| Maximized_ClientExactlyCoversWorkArea_InDipUnits_IsMaximized (:37) | ✅ | Wayland DIP 报法识别 |
| Maximized_ClientCoversWorkArea_InPhysicalUnits_IsMaximized (:45) | ✅ | 物理像素报法 + 缩放换算识别 |
| Maximized_TiledOrFloatingWindow_KeepsCorners (:57, T×4) | ✅ | 平铺/浮动（含 Hyprland gap 22 实测）保留圆角——去圆角误生效事故回归 |
| Maximized_WithinRoundingTolerance_IsMaximized (:70, T×3) | ✅ | 1-2px 舍入容差内仍算铺满 |
| Maximized_OnlyOneDimensionMatches_IsNotMaximized (:78) | ✅ | 宽高必须同时吻合 |
| Maximized_InvalidScaling_TreatsWorkAreaAsDip (:89, T×3) | ✅ | 缩放 null/0/NaN 视为 DIP（匹配不失效） |
