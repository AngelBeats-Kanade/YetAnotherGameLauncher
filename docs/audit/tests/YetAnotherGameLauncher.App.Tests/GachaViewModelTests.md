# GachaViewModelTests 审计

- 方法数：3；判定：✅ 3
- 形态：直调；StubHttpHandler 全离线；统计断言确定（7 池 × 每池 2 条）。

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| Constructor_BuildsAllPoolOptions (:48) | ✅ | 8 个卡池选项（全部+1-7）、默认选中、标题 |
| RefreshAsync_NoUrl_ShowsGuidanceHint (:59) | ✅ | 无地址 → 引导提示（hint 样式）、零记录 |
| RefreshAsync_WithLogAndStub_FetchesMergesAndComputesStats (:73) | ✅ | 日志地址提取 → 拉取合并（14 条）→ 五星/四星统计 → 池筛选 → 时间倒序与保底计数 |
