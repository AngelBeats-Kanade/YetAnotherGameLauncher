# LinuxAutostartServiceTests / WindowsAutostartServiceTests 审计

## LinuxAutostartServiceTests（3 ✅）
home/exe 全注入隔离，XDG desktop 入口真实文件往返。

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| SetEnabledTrue_WritesDesktopEntry (:15) | ✅ | 写 desktop 入口（Exec 引号 + Autostart 标志）+ IsEnabled 回读 |
| SetEnabledFalse_RemovesDesktopEntry (:30) | ✅ | 关闭移除入口 + 状态翻转 |
| IsEnabledAsync_NoEntry_ReturnsFalse (:43) | ✅ | 无入口 false |

## WindowsAutostartServiceTests（4 ✅）
经 FakeProcessRunner 断言 reg 命令构造（**命令形状级**测试，任何 OS 确定性跑——与
SettingsAutostartTests 的真注册表查询形成互补：形状在这里锁死、环境敏感性在那里暴露）。

| 方法 | 判定 | 守卫行为 |
|---|---|---|
| IsEnabledAsync_QueriesRunKey_ReturnsExitCodeZero (:19) | ✅ | reg query HKCU Run 命令形状 + exit 0 语义 |
| IsEnabledAsync_KeyMissing_ReturnsFalse (:30) | ✅ | exit 1 = 键缺失 |
| SetEnabledAsync_AddsRunEntryWithQuotedExe (:38) | ✅ | reg add + REG_SZ + exe 引号 |
| SetEnabledAsync_Disabled_DeletesRunEntry (:49) | ✅ | reg delete |
