# StartupAssetPreloadTests 审计

- 方法数：2；判定：❌ 2
- 命中机制：D1（async lambda 吞断言）+ 资源竞态（`using var ctx` 在 lambda 内）

| 方法 | 判定 | 证据 | 修复方案 | 守卫行为 |
|---|---|---|---|---|
| InitializeAsync_PreloadsAllIcons_AndChecksVersionOncePerGame (:53) | ❌ D1 | :57 Dispatch(async)，:63 await 后 :66-78 断言全在内；`using var ctx`(:59) 在 lambda 内与孤儿续体竞态 | ctx 提到测试方法体；`HeadlessSession.Instance` 触发初始化后测试线程直调 `InitializeAsync`；断言外置 | 启动预热：全部游戏图标加载、每游戏版本检测恰一次、切换选中走会话缓存零网络 |
| VersionChange_RefetchesIconAndResolvesBackdrop_Unchanged_SkipsAll (:83) | ❌ D1 | :85 Dispatch(async)，:93/:100/:106 await，:96-110 断言全在内 | 同上 | 版本门控：版本不变零网络零解析；版本变化重解析背景 + http 图标绕缓存重取 |
