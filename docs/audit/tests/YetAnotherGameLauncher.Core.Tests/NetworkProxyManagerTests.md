# NetworkProxyManagerTests 审计

- 方法数：4；判定：✅ 4

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| Apply_System_FollowsSystemDefaults (:11) | ✅ | System：UseProxy=true 且不指定（系统默认解析） |
| Apply_None_DisablesProxy (:22) | ✅ | None：禁用代理 |
| Apply_ManualWithValidAddress_UsesWebProxy (:33) | ✅ | Manual：WebProxy 地址 |
| Apply_ManualWithInvalidAddress_FallsBackToSystem (:45) | ✅ | 非法地址回退系统默认 |
