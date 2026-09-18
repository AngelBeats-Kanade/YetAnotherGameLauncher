# Core/Services/SystemProcessRunner.cs 逐行审计（319 行，2026-09-19）

全仓进程语义的基石（AGENTS.md 三坑防线的载体）。逐段审计如下；63 行未覆盖中约 45 行为
Windows 专属提权回退路径（Linux CI 结构性不可达），其余为容错分支与事件竞态收尾。

| 行 | 判定 | 论证 | 覆盖 |
|---|---|---|---|
| 25-51 日志头写入 | ✅ 逻辑 | logDir 创建、头部三行 + env 块——被日志落盘测试断言（# command/out-line/[stderr]/退出脚注） | **未覆盖 44-50**（env 块写入：现有 fire-and-forget 测试未带环境变量 + 日志）。Phase 4：日志路径 + 自定义 env 的用例 |
| 53-73 StartInfo 组装 | ✅ 逻辑 | redirectOutput 判定（等待模式恒真；即启即走仅当日志路径存在——防管道缓冲卡死注释自证）；环境变量仅在非空时触碰（L67 提权路径保护，注释详实） | **未覆盖 68,70-73**（env 注入循环：等待模式测试未传自定义 env）。Phase 4：等待模式 + env 用例（顺带覆盖 44-50） |
| 86-131 提权回退（740） | ✅ 逻辑 / ⚠️ Windows 专属 | catch 过滤条件精确（!waitForExit && 740 && 支持）；env 不可注入时警告；日志壳写明原因后释放（防误导秒退排查）；兄弟 catch 接不到 catch 内异常 → 内层自带 dispose 兜底（L119-128 注释自证——这是 C# 异常处理的真实语义约束）；CreateElevatedStartInfo 的"绝不携带自定义环境"契约有直接单测 | **未覆盖 87-133 整段**（Linux 结构性不可达；supportsElevationRetry=false 可绕但 740 无法在 Linux 构造）。处置：**列入 Windows-CI 可达清单**，Phase 4 尝试用清单 requireAdministrator 的桩 exe 在 Windows 腿覆盖；无法覆盖部分逐行论证保留（740 为 Windows 安装器清单语义，无法在 POSIX 模拟） |
| 132-137 启动失败释放 | ✅ | 日志句柄泄漏防线（FileNotFound 路径经由 tool-missing 测试触达过 StartInfo 组装；此 catch 在 Linux 上可由文件不存在触发） | **未覆盖 136**——Phase 4：不存在的可执行文件 + 日志路径（EnsureProton 场景已很接近，差一个直调用例） |
| 139-162 带日志即启即走 | ✅ 逻辑 | 自管 ReadLine 泵（BeginOutputReadLine 丢 stderr 的实锤防线）；`HasExited` 布防前退出补查（进程竞态实锤防线，L150-154）；Exited 挂钩 | 已覆盖（日志落盘测试三条断言）；**未覆盖 156-159**（Exited 挂钩分支——测试中的进程恰好在 HasExited 补查前未退出，走了 150-154 对称分支；两分支语义相同）。Phase 4：长 sleep 进程 + 延迟读取日志可稳定命中 |
| 164-181 无日志即启即走 | ✅ 逻辑 | 进程句柄不主动释放（Dispose 解除 Exited 布防——注释自证），交终结器 | **未覆盖 172-174**（HasExited 补查分支，同上竞态）。Phase 4：即启即走 + 瞬秒命令 |
| 183-218 等待模式 | ✅ | 超时杀整树 + 传播取消（Timeout 测试）；stdout/stderr 全量读取（WaitForExit 测试断言退出码）；InvalidOperationException→已退出吞掉（L203-206 竞态容错） | **未覆盖 203-204,206,209**（取消时 Kill 抛 InvalidOperation 的竞态容错 + rethrow——需超时与进程退出同时命中，窗口极窄；rethrow 行 209 本身应可达但被 timeoutCts 语义合并）。Phase 4：尝试极短 Timeout 与极快进程的组合；打不中按等价变异豁免论证 |
| 226-233 CreateElevatedStartInfo | ✅ | internal 直测（身份保留/ShellExecute/无自定义环境） | 已覆盖 |
| 236-273 ObserveFireAndForgetExit | ✅ 逻辑 | 泵收尾→脚注→Dispose 的顺序契约（日志落盘测试的"exited with code 7"断言依赖此链） | **未覆盖 259-261**（写脚注时 ObjectDisposed 容错）、**268-269,271**（Dispose 容错）——均为释放竞态防御。Phase 4 难以确定性构造；变异验证时按容错等价类论证 |
| 276-298 LogFireAndForgetExit / AliveSeconds | ✅ | 秒退排查线索；句柄不可用回退 0/0 | **未覆盖 292-293,295**（ExitCode 抛出容错）。同上竞态类 |
| 301-318 PumpToLog | ✅ | 逐行 + gate 串行化 + 已关闭停止（多行输出测试覆盖主路径） | **未覆盖 312-314**（ObjectDisposed 停泵——与 259-261 同族竞态） |

## 处置汇总

- **Windows-CI 可达**：提权回退整段（~45 行）——Phase 4 在 Windows 腿用 requireAdministrator 桩 exe 尝试；
- **竞态容错类（~10 行）**：268-271/292-295/312-314/203-209——构造窗口极窄，Phase 4 先试，
  打不中则逐行给"容错等价类"论证（防御代码删除后行为仅在进程竞态窗口可见，属可接受残留）；
- **可直接补测（~8 行）**：44-50/68-73（env+日志组合）、136（启动失败释放）、156-159/172-174（瞬秒进程）。
