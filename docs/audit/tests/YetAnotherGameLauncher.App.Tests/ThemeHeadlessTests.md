# ThemeHeadlessTests 审计

- 方法数：3；判定：⚠️ 3（全文件 D2：断言在 Dispatch(Action) 内，Probe1 实锤可传播、当前有效）

| 方法 | 判定 | 证据 | 修复方案 | 守卫行为 |
|---|---|---|---|---|
| Apply_Dark_SetsDarkActualVariant (:14) | ⚠️ D2 | :22 断言在内 | 断言外移（创建/Apply 在内，读 ActualThemeVariant 存局部、外断） | Apply(Dark) → ActualThemeVariant=Dark |
| Apply_Light_SetsLightActualVariant (:27) | ⚠️ D2 | :35 同上 | 同上 | Apply(Light) → Light |
| Apply_System_UsesDefaultRequestedVariant (:40) | ⚠️ D2 | :50 同上（断 Requested 而非 Actual，语义正确：System=跟随平台） | 同上 | Apply(System) → RequestedThemeVariant=Default |
