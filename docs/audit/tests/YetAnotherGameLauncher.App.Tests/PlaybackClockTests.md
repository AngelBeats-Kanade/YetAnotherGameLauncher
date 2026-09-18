# PlaybackClockTests 审计

- 方法数：6；判定：✅ 3 / ⚠️ 3
- 主题：背景视频 PTS 实时节拍器（首帧立即、delay 映射、大落后重定基线、负值钳制、倍速）。

| 方法 | 判定 | 证据 | 修复方案 | 守卫行为 |
|---|---|---|---|---|
| FirstFrame_RendersImmediately_NextFrameEntersPacing (:13) | ✅ | :18-22 等待量 `<=1` 对调度停顿鲁棒（停顿只会更负→钳 0） | 无 | 首帧立即渲染；同刻后续帧等待量归零 |
| Delay_MapsPtsToWallClock (:26) | ⚠️ D5 | :36 `InRange(1900,2000)` 依赖两次调用间真实墙钟——CI 停顿 >100ms 即假红 | PlaybackClock 若可注入 TimeProvider 则换 ManualTimeProvider（SpeedLimiter 先例）确定化；否则放宽容差并注释依据 | pts 间隔 → 墙钟等待量换算 |
| LargeLag_RebasesInsteadOfBursting (:44) | ⚠️ D5 | :55 `InRange(50,100)` 同上（容差 ±50ms 更紧） | 同上 | EOF 回卷大落后 → 重定基线立即渲染而非爆发追帧 |
| Reset_ClearsBaseline (:59) | ✅ | :62-66 只断 null/非 null，无墙钟区间 | 无 | Reset 后重新取基线 |
| SlightlyBehind_ClampsToZero (:70) | ✅ | :76-78 负 pts 钳 0，确定性行为 | 无 | 轻微落后钳 0 不为负 |
| Rate_ScalesWaitTime (:82) | ⚠️ D5 | :90 `InRange(150,250)` 真实墙钟 | 同上 | 倍速缩放等待量 |
