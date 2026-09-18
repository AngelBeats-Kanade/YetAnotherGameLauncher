# NativeUmuLauncherLaunchTests 审计

- 方法数：1；判定：✅ 1

| 方法 | 判定 | 证据 | 修复方案 | 守卫行为 |
|---|---|---|---|---|
| BuildPlan_EmitsContainerEntryAndExpandedEnv (:16) | ✅ | 平台分支**双腿各有真实断言**（:18-23 Windows 腿断言 BuildEntryCommand 纯逻辑；Linux 腿断言完整 BuildPlan）——与 NativeUmuLaunchRoutingTests 的零断言早退（❌）形成正反对照 | 无 | 容器入口/参数/工作目录/env 全链（含 PROTONPATH 代号不得覆盖已解析绝对路径、CUSTOM {installDir} 展开、umuId 不迁移 prefix） |
