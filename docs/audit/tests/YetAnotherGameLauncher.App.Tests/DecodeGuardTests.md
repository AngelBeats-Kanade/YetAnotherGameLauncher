# DecodeGuardTests 审计

- 方法数：6；判定：✅ 6
- 形态：纯逻辑熔断器直调，全确定。

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| ConsecutivePacketsWithoutFrame_TripAtThreshold (:14) | ✅ | 阈值-1 不判死（容纳 B 帧重排）、第 N 个判死 |
| DecodedFrame_ResetsStallCounter (:29) | ✅ | 出帧清零计数 |
| ViablePass_ResetsDeadPassCount (:47) | ✅ | 有效回（fps 折算阈值）清失败计数；3 回无效才停 |
| DeadPasses_TripAtThirdConsecutive (:64) | ✅ | 连续第 3 回无效停播 |
| Reset_ClearsDeadPassCount (:75) | ✅ | Reset（预卷健康接管）清零 |
| MinViablePassFrames_FpsIndependentFloorAndScale (:89) | ✅ | 未知帧率地板 8 帧；60fps→15 帧 |
