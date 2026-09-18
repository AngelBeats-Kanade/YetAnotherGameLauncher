# LaunchErrorOverlayTests 审计

- 方法数：4；判定：✅ 3 / ⚠️ 1
- 主题：启动失败 UX（不可启动不静默、预检失败弹覆盖层、可关闭、成功清残留）。

| 方法 | 判定 | 证据 | 修复方案 | 守卫行为 |
|---|---|---|---|---|
| LaunchAsync_WhenNotReady_ShowsReasonInsteadOfSilentReturn (:21) | ✅ | :30-32 状态文案含"主程序"且覆盖层为 null（未到预检不弹） | 无 | 不可启动给出原因且不误弹覆盖层 |
| LaunchAsync_RuntimeMissing_ShowsOverlay (:36) | ✅ | :42 wine 模板 + PATH 禁用注入；:46-50 覆盖层消息含 wine、详情可展开、无日志路径 | 无 | 运行时缺失 → 类目化覆盖层 |
| LaunchAsync_DismissHidesOverlay (:54) | ⚠️ D8 | :64 `LaunchError.DismissCommand.Execute(null)` 纯 VM 层——覆盖层控件（LaunchErrorOverlay.axaml）的关闭按钮绑定若断裂，此测试照绿（Toast IsHitTestVisible 实锤的同族风险） | 补真实指针命中链路测试（MouseMove/Down/Up 点覆盖层关闭钮，先例 ToastHeadlessTests） | 覆盖层可关闭（视图链路） |
| LaunchAsync_Success_ClearsStaleOverlay (:70) | ✅ | :82 换直启模板走成功路径，:86-87 覆盖层清掉 + 状态=已启动 | 无 | 成功启动清除残留覆盖层 |
