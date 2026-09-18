# SystemProcessRunnerTests 审计

- 方法数：5；判定：✅ 4 / ❌ 1
- 形态：真实子进程（唯一不打桩的进程测试）+ 双平台命令选择（双腿各有断言）+ 时长上界。

| 方法 | 判定 | 证据 | 修复方案 | 守卫行为 |
|---|---|---|---|---|
| RunAsync_FireAndForget_ReturnsBeforeProcessExits (:11) | ✅ | 平台选择命令（ping/sleep 30）双腿真实 | 无 | 即启即走不等退出（10 分钟杀树事故回归） |
| RunAsync_FireAndForget_WritesOutputToLogFile (:31) | ❌ D3 | :34-37 `if (IsWindows) return;`——日志落盘行为**跨平台可测**（Windows 可用 cmd 多路输出或标记可见跳过），当前 Windows CI 零断言静默绿 | 补 Windows 腿（cmd /c 双路输出 + 退出码）或 Assert.Skip 显式化 | 即启即走进程的 stdout/stderr/退出脚注全部落盘（自管 ReadLine 泵的三坑防线） |
| RunAsync_WaitForExit_ReturnsExitCode (:72) | ✅ | 双平台 exit 7 | 无 | 等待模式返回真实退出码 |
| CreateElevatedStartInfo_KeepsIdentityAndSwitchesToShellExecute (:85) | ✅ | | 无 | 提权回退：ShellExecute 语义 + 保留名参目录 + 自定义环境绝不注入 |
| RunAsync_Timeout_KillsProcessAndReportsCancellation (:108) | ✅ | 长进程 + 500ms 超时，双平台 | 无 | 超时杀进程树 + 抛取消 + 及时性 |
