# VideoBackdropHeadlessTests 审计

- 方法数：4；判定：❌ 2 / ⚠️ 2
- 命中机制：D7/G9（AssertSurfaceVisible 静默 return）+ D2（Dispatch(Action) 内断言——Probe1 实锤可传播，违纪但有效）

| 方法 | 判定 | 证据 | 修复方案 | 守卫行为 |
|---|---|---|---|---|
| VideoSource_PlaysWhenDetailVisible_StopsWhenLeaving (:30) | ❌ D7（+D2） | :60/:67 调 AssertSurfaceVisible；helper :198-200 `if (game.VideoPlayer is null) return;`——播放器注入链一旦断裂，可见性断言整组静默蒸发（测试仍绿，但"视频层可见"这一 UX 契约失守）；其余断言（PlayedPaths/HasBackgroundVideo/StopCount）仍有效 | helper 首行改 `Assert.NotNull(game.VideoPlayer)`；断言外移（D2 全文件统一处理） | 进详情页起播→首帧点亮视频层→切页停止且层隐藏 |
| VideoSource_ReentersDetailPage_ResumesPlayback (:74) | ⚠️ D2 | :92-102 断言在 Dispatch(Action) 内（可传播）；不调 helper，无 D7 暴露 | 断言外移 | 切回详情页凭已解析路径恢复播放 |
| ImageSource_PlayerNeverInvoked (:109) | ⚠️ D2 | :124-125 断言在内；不调 helper | 断言外移 | 静态图来源不触碰播放器 |
| VideoSource_SwitchingGames_LateStaleNotifyDoesNotLightNewGame (:131) | ❌ D7（+D2） | :171/:177/:184 三处调 helper，同 :30 的静默蒸发风险；且本测试守卫的正是"停止即清帧"共享单例契约（ARCHITECTURE §3.7），可见性断言是其核心 | 同 :30 | 共享播放器切游戏：陈旧帧通知不得点亮新游戏视频层 |
