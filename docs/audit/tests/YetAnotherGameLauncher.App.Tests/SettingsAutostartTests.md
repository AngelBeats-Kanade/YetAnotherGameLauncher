# SettingsAutostartTests 审计

- 方法数：2；判定：⚠️ 2（环境敏感）
- 亮点：双平台**各用真实实现**（Windows 真 HKCU reg query / Linux 真 XDG + 临时 home）——
  这是平台分支的正确处理形态（两腿都有真实断言，非 D3 静默跳过）；有界 deadline 轮询合规。

| 方法 | 判定 | 证据 | 修复方案 | 守卫行为 |
|---|---|---|---|---|
| ShowSettings_WithRealRegistryQuery_InitializesAutostartState (:17) | ⚠️ G-环境 | :34-36 断言 `IsAutostart is false`——隐含"本机未注册自启"前提：开发机若开启过自启，轮询超时假红（CI 干净所以没暴露） | Windows 实现注入可替换的注册表查询入口（或查询前自清理/记录-还原），消除对本机状态的隐含依赖 | 设置页打开异步补齐自启状态且不阻塞（sync-over-async 死锁事故回归） |
| AutostartService_QueryRealRegistry_Completes (:40) | ⚠️ G-环境 | :48 同上假设（键存在会返回 true 使断言假红） | 同上 | 各平台真实只读查询能完成并返回 |
