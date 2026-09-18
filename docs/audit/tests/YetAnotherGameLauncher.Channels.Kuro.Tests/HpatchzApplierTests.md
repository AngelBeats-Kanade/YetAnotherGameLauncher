# HpatchzApplierTests 审计

- 方法数：6；判定：✅ 5 / ⚠️ 1
- 形态：FakeProcessRunner 命令形状断言 + 真实文件桩。

| 方法 | 判定 | 证据 | 修复方案 | 守卫行为 |
|---|---|---|---|---|
| ApplyAsync_BuildsDirectoryModeCommandWithQuotedPaths (:28) | ✅ | | 无 | 目录模式命令 + 三路径引号 + -f |
| ApplyAsync_CreatesOutputDirectory (:46) | ✅ | | 无 | 输出目录前置创建 |
| ApplyAsync_NonZeroExit_ThrowsWithStderr (:58) | ✅ | | 无 | 非零退出码 + stderr 进错误消息 |
| ApplyAsync_PassesConfiguredTimeout (:73) | ✅ | | 无 | 超时配置透传 |
| ApplyAsync_ToolMissing_ThrowsWithActionableHint_BeforeSpawning (:89) | ✅ | :101 零进程调用断言 | 无 | 工具缺失预检先行（含 .exe 补试语义被本组覆盖的前提） |
| ApplyAsync_ToolWithoutExecBit_Linux_ThrowsBeforeSpawning (:105) | ⚠️ D3' | :107-110 Windows 静默早退——但执行位是 POSIX 专属概念，Windows 无对应断言 | 改 Assert.Skip 显式化 | Linux 缺执行位不触进程即报错 |
