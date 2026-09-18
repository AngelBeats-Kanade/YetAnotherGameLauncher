# ToastTests 审计

- 方法数：5；判定：✅ 4 / ⚠️ 1

| 方法 | 判定 | 证据 | 修复方案 | 守卫行为 |
|---|---|---|---|---|
| InitializeAsync_DoesNotToastOnFirstRefresh (:22) | ✅ | :26 首轮预热静默 | 无 | 启动预热不弹 toast（armed 门） |
| ShowToast_CapsAtThree_DropsOldest (:30) | ✅ | :37-41 容量 3、丢最旧、种类旗标 | 无 | toast 容量上限与过载策略 |
| DismissCommand_RemovesToast (:45) | ⚠️ D8 | :50 VM 层 Execute——视图命中链路断裂拦不住；视图层补充已存在（ToastHeadlessTests 真实指针回归），本条作为 VM 语义测试保留 | 无（补充已存在，记录在案） | DismissCommand 移除对应 toast |
| StatusChange_AfterFirstRefresh_RaisesToast (:56) | ✅ | :59-81 armed 门 + 状态翻转弹 + 语义未变不重弹（:80-81 回归断言） | 无 | 状态变化轻提示与去重 |
| ServerSwitch_RaisesToastWithServerName (:85) | ✅ | :96-102 注入第二服务器驱动切换 + Info 种类 + B服文案 | 无 | 服务器切换提示 |
