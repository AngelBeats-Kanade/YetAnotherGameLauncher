# Core/Services/GameLauncherService.cs 逐行审计（221 行，2026-09-19）

| 行 | 判定 | 论证 | 覆盖 |
|---|---|---|---|
| 17-27 构造 | ✅ | logDirectory 缺省进 DataDirectory/logs；pathValue 注入语义（null=真实环境，""=禁扫描）有专门测试 | 已覆盖 |
| 30-64 BuildPlan | ✅ | exe 路径归一（正斜杠→原生）后 File.Exists 预检；模板空抛 UpdateException；env 值逐项 Expand + Ordinal 字典；prefix 目录前置创建。LaunchParameterRoundTripTests 端到端 + 单元用例群 | 主体已覆盖；**未覆盖 43-44**（模板空抛 UpdateException——配置层 Validate 已拦空模板，此处为纵深防御，理论可达：直接构造绕过校验的 GameDefinition。Phase 4 补直调用例） |
| 70-92 LaunchAsync | ✅ | 即启即走（WaitForExit:false 的 10 分钟杀树事故回归）；Win32Exception → StartFailed 包装（Win32Exception(13) 注入测试）；日志路径挂接 | 已覆盖 |
| 98-147 ValidateCommand | ✅ 逻辑 | 裸命令：Windows 生产（pathValue=null）交 CreateProcess 解析（AGENTS.md 语义）；注入 pathValue 恒走扫描；绝对路径：不存在→RuntimeMissing；Linux 缺执行位自动补 +x、补失败给 chmod 指引 | 主体已覆盖；**未覆盖 106-107**（Windows 生产早退分支——Linux CI 不可达，Windows CI 可达。Phase 4：Windows 腿）；**未覆盖 138-144**（自动补执行位失败→RuntimeNotExecutable：需要只读父目录的绝对路径脚本。Phase 4 补用例） |
| 150-172 EnsurePrefixDirectories | ✅ 逻辑 | WINEPREFIX/STEAM_COMPAT_DATA_PATH 前置创建；创建失败归类 PrefixCreateFailed | 创建成功已覆盖（BuildPlan_CreatesWinePrefixDirectory）；**未覆盖 163-169**（失败分支：需 CreateDirectory 抛 IO 异常——注入难度高。Phase 4：把 key 列表指向不可创建路径（如以文件为父目录）构造 ArgumentException 路径） |
| 175-177 ComposeLogPath | ✅ | SanitizeGameId + 时间戳，被日志文件名正则测试覆盖 | 已覆盖 |
| 181-188 Expand | ✅ | 引号兼容（已手写引号不双引）；{installDir} 字面替换（不归一分隔符——LaunchParameterRoundTripTests 斜杠语义注释自证） | 已覆盖 |
| 191-212 SplitCommand | ✅ | 引号/空格/裸命令三 Theory；空串抛 ArgumentException | 主体已覆盖；**未覆盖 195-196**（空命令抛参——纯防御）、**206**（引号内无空格的裸段）。Phase 4：两个一行的直调用例 |

结论：无实锤 bug。21 行未覆盖全部为容错/防御分支与 Windows 生产专属路径，已列 Phase 4 清单
（预计可补 17 行；Windows 腿 2 行依赖 Windows CI）。
