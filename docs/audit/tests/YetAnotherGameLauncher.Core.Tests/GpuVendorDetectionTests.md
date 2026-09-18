# GpuVendorDetectionTests 审计

- 方法数：9；判定：✅ 9
- 形态：/proc 与 /sys 路径全部可注入（Windows 测试机覆盖 Linux 分支——平台分支测试的正确范式）。

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| GpuVendors_AmdCardViaSysfs_ReturnsAmd (:19) | ✅ | sysfs PCI vendor → AMD |
| GpuVendors_IntelCardViaSysfs_ReturnsIntel (:28) | ✅ | → Intel |
| GpuVendors_NvidiaCardViaSysfs_ReturnsNvidia (:37) | ✅ | → NVIDIA |
| GpuVendors_HybridLaptop_MergesProcNvidiaWithSysfsAmd (:46) | ✅ | 双卡合并（/proc 闭源 + sysfs 全量） |
| GpuVendors_DuplicateVendorAcrossCards_Deduplicates (:61) | ✅ | 跨卡去重 |
| GpuVendors_MalformedVendorFile_IsIgnored (:70) | ✅ | 坏内容忽略 |
| GpuVendors_UnknownVendorId_IsIgnored (:79) | ✅ | 未知厂商 id 忽略 |
| GpuVendors_NoCards_ReturnsEmpty (:87) | ✅ | 无卡空集 |
| GpuVendors_NoNvidiaProcFile_SysfsAlone (:93) | ✅ | 无 /proc 文件时仅 sysfs |
