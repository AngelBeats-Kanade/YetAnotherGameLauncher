# LinuxPlatformInfoTests / PlatformInfoFactoryTests 审计

## LinuxPlatformInfoTests（3 ✅）

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| IsNvidiaGpuPresent_ProbeFileExists_ReturnsTrue (:15) | ✅ | /proc 探测文件存在 → true（路径注入） |
| IsNvidiaGpuPresent_ProbeFileMissing_ReturnsFalse (:27) | ✅ | 缺失 → false |
| IsLinux_AlwaysTrue (:36) | ✅ | 平台标识常真 |

## PlatformInfoFactoryTests（1 ✅）

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| Create_MatchesCurrentOs (:9) | ✅ | 全仓唯一平台分支：落地类型与当前 OS 一致（平台分支测试的正确收口形态） |
