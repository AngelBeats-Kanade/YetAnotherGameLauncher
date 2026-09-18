# SpeedLimiterTests 审计

- 方法数：3；判定：✅ 3
- 形态：ManualTimeProvider 虚拟时钟（墙钟无关，全确定——PlaybackClockTests 该学的样板）。

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| Acquire_Unlimited_ReturnsZero (:10) | ✅ | 不限速零等待 |
| Acquire_QueuesBeyondBudget (:20) | ✅ | 超预算排队等待（区间断言）+ 虚拟时钟推进后放行 |
| SetZero_ResetQueue (:39) | ✅ | 切换不限速清空排队 |
