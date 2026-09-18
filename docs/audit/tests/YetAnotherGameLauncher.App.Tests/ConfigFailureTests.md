# ConfigFailureTests 审计

- 方法数：4；判定：✅ 3 / ⚠️ 1
- 主题：配置失败路径绝不写盘（用户手改 games.json 是受支持工作流）。

| 方法 | 判定 | 证据 | 修复方案 | 守卫行为 |
|---|---|---|---|---|
| InitializeAsync_InvalidConfig_NeverOverwritesUserFile (:21) | ✅ | :25-32 前后全文对比 + 错误态断言，直调可传播 | 无 | 校验失败不覆盖用户文件 + 错误状态提示 |
| InitializeAsync_InvalidConfig_PersistWindowState_NeverWritesFile (:36) | ✅ | :46 同步调用后 :48 文件未变 | 无 | 失败态关窗回写被 Catalog null 防线拦截 |
| InitializeAsync_InvalidConfig_SidebarToggle_NeverWritesFile (:52) | ⚠️ D5 | :63 fire-and-forget setter 后 :64 固定 `Task.Delay(400)` 再负向断言——若防线回归且写盘慢于 400ms，测试漏检 | 改有界条件轮询（写盘事件一旦发生立即失败；轮询上限后断言未发生） | 失败态任何设置保存不落盘 |
| InitializeAsync_ConfigPathUnreadable_ShowsErrorWithoutCrash (:70) | ✅ | :74 配置路径占位成目录制造必然 IO 失败，:78-80 断言错误态 | 无 | 配置读取 IO 失败 → 错误态不崩溃 |
