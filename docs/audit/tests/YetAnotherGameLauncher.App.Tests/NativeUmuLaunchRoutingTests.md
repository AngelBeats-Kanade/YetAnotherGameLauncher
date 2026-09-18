# NativeUmuLaunchRoutingTests 审计

- 方法数：1；判定：❌ 1（D3）

| 方法 | 判定 | 证据 | 修复方案 | 守卫行为 |
|---|---|---|---|---|
| SavedNativeUmuSettings_LandInFinalContainerCommandAndEnv (:26) | ❌ D3 | :28-31 `if (!OperatingSystem.IsLinux()) return;`——Windows CI 上零断言静默绿（比 Skip 更糟：摘要里显示为已通过）。注释以"Windows 走 NativeUmuCoreTests 纯逻辑分支"自辩，但本测试守卫的 LaunchAsync→NativeUmuLauncher 端到端路由在 Windows CI 上从未执行 | 双平台断言改造：Linux 腿照旧；Windows 腿反向断言（同配置 LaunchAsync 不得产出 umu 容器命令——保护生产路由的平台门控本身），两条腿都真实断言 | 启动设置卡参数（native-umu/PROTONPATH/自定义 env/umuId）→ 最终容器命令与环境逐项（含 AGENTS.md 实锤的 launch.umuId 保留回归） |
