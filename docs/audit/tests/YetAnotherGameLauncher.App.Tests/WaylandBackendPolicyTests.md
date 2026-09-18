# WaylandBackendPolicyTests 审计

- 方法数：6（2 Fact + 3 Theory，案例互异有意义）；判定：✅ 6
- 形态：纯函数决策表直调，覆盖决策矩阵全格。

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| Linux_WithWaylandDisplay_UsesNativeWayland (:14) | ✅ | Linux + WAYLAND_DISPLAY → 原生 Wayland |
| Linux_WithoutUsableWaylandDisplay_FallsBackToX11 (:24, T×3) | ✅ | null/空串/空白 → X11 回退（无自动回退的后端不能盲启） |
| Linux_ForceXwaylandEscapeHatch_FallsBackToX11 (:34, T×3) | ✅ | 逃生舱 1/true/TRUE → X11 |
| Linux_UnsetOrNegativeEscapeHatch_DoesNotAffectDecision (:45, T×4) | ✅ | 未设/0/false/no 不影响决策 |
| NonLinux_NeverUsesNativeWayland (:52) | ✅ | Windows（含 WSL 残留变量）绝不走 Wayland |
| ForceXwaylandVariableName_IsStableContract (:60) | ✅ | 逃生舱变量名是文档承诺，改名即破坏契约 |
