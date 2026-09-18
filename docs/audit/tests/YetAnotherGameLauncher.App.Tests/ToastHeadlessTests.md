# ToastHeadlessTests 审计

- 方法数：1；判定：⚠️ 1（D2）

| 方法 | 判定 | 证据 | 修复方案 | 守卫行为 |
|---|---|---|---|---|
| ClickingCloseButton_RemovesToast_ViaRealHitTesting (:31) | ⚠️ D2 | :47/:55 断言在 Dispatch(Action) 内——**可传播**（Probe1），本测试是 AGENTS.md 实锤 IsHitTestVisible 剪枝事故后的真实指针命中回归，守卫价值高且当前真实有效 | 断言外移（ShowToast/命中点击在内，Single/Empty 外断），对齐纪律 | toast 关闭钮必须经真实命中链路可点（防宿主穿透回归） |
