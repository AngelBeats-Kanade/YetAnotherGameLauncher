# PrerollHandoffTests 审计

- 方法数：7；判定：✅ 7
- 形态：所有权状态机直调；:96 跨线程交错测试用 WaitAsync(10s) 有界等待交付信号后再收编
  （:98-100 注释自述"不依赖两段 Delay 相对时长"——D5 合规形态）。

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| CompleteThenTake_TransfersOwnership_WithoutCleanup (:20) | ✅ | 收编移交所有权、回调不触发、二次收编失败 |
| TakeWhileRunning_Fails (:35) | ✅ | 生产中不可收编 |
| AbandonWhileRunning_ProducerCompletionCleansPayload (:44) | ✅ | 放弃后生产者完成 → 负载就地清理且不可再收编 |
| AbandonWhenCompleted_CleansImmediately (:56) | ✅ | 已完成后放弃 → 立即清理 |
| AbandonTwice_CleansOnlyOnce (:68) | ✅ | 恰好一次语义 |
| CompleteTwice_SecondPayloadIsRejectedAndCleaned (:80) | ✅ | 二次交付拒绝且清理新负载、首个负载完好 |
| CrossThreadHandoff_ProducerCompletes_ConsumerTakes (:96) | ✅ | 跨线程交付-收编（有界等待） |
