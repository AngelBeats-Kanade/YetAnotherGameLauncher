# LaunchParameterRoundTripTests 审计

- 方法数：1；判定：✅ 1

| 方法 | 判定 | 证据 | 修复方案 | 守卫行为 |
|---|---|---|---|---|
| SavedLaunchSettings_DriveBuildPlanExactly (:25) | ✅ | UI 草稿 → SaveAsync → 磁盘重载 → BuildPlan 全链逐项断言（:62-79）；含空格目录的 {exe} 引号展开；分隔符两侧同一归一表达式构造（:33-35 注释自证）；UnixFileMode 平台分支是真实平台要求而非静默跳过（其余断言双平台都跑） | 无 | "参数按配置进入游戏"的最短证明链：落盘即事实源 |
