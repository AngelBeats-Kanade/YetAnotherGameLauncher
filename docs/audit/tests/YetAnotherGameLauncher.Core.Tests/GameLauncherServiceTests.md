# GameLauncherServiceTests 审计

- 方法数：15（14 Fact + 1 Theory×3）；判定：✅ 14 / ⚠️ 1

| 方法 | 判定 | 证据 | 修复方案 | 守卫行为 |
|---|---|---|---|---|
| BuildPlan_ExpandsExePlaceholder (:46) | ✅ | | 无 | {exe} 展开 + 相对可执行解析 |
| BuildPlan_BareExeTemplate_QuotesSpacedPaths (:58) | ✅ | | 无 | 含空格路径不被截断 |
| BuildPlan_WineTemplate_SplitsCommandAndArgs (:74) | ✅ | 注入 PATH 桩带执行位 | 无 | wine 模板拆分命令与参数 + 引号 |
| BuildPlan_ExpandsEnvironmentValues (:89) | ✅ | | 无 | env 值占位符展开 |
| BuildPlan_WorkingDirectoryFallsBackToInstallDir (:99) | ✅ | | 无 | 空工作目录回退安装目录 |
| BuildPlan_MissingExecutable_ThrowsCategorized (:111) | ✅ | | 无 | 主程序缺失 → ExecutableMissing |
| BuildPlan_BareRuntimeMissingOnPath_ThrowsCategorized (:121) | ✅ | pathValue:"" 注入即禁扫描（AGENTS.md 语义） | 无 | 裸运行时缺失 → RuntimeMissing |
| BuildPlan_AbsoluteRuntimeMissing_ThrowsCategorized (:134) | ✅ | | 无 | 绝对路径运行时缺失同类 |
| BuildPlan_AbsoluteRuntimeWithoutExecBit_IsFixedAutomatically (:147) | ⚠️ D3' | :150-153 `if (IsWindows) return;` 静默零断言——但守卫行为（补 +x）本质 POSIX 专属，Windows 上无对应语义可断言 | 改 xunit.v3 `Assert.Skip`（摘要在 Skipped 列可见）或平台标注，把" Windows 上没跑"显式化 | 解包脚本缺执行位 → 预检自动补 +x |
| BuildPlan_CreatesWinePrefixDirectory (:169) | ✅ | | 无 | STEAM_COMPAT_DATA_PATH 前置创建 |
| LaunchAsync_ReturnsProcessExitCodeAndLogPath (:183) | ✅ | | 无 | 退出码/日志路径/工作目录/日志挂接 |
| LaunchAsync_IsFireAndForget (:199) | ✅ | WaitForExit=false（10 分钟杀进程树事故回归） | 无 | 游戏启动不等进程退出 |
| LaunchAsync_StartFailure_WrappedAsCategorizedError (:211) | ✅ | Win32Exception 包装为 StartFailed | 无 | 启动失败归类 |
| SanitizeGameId_UnsafeCharacters_ReplacedForLogFileName (:227) | ✅ | 文件名正则断言 | 无 | 游戏 id 清洗进日志文件名 |
| SplitCommand_HandlesQuotesAndArguments (:245, T×3) | ✅ | 引号/裸命令/带参 | 无 | 命令行拆分 |
