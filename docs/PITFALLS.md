# PITFALLS —— 跨平台 / .NET 踩坑手册

语言与平台层**通用坑**的单一事实源（AGENTS.md「坑索引」只留一行触发器；UI/无头测试坑在
`.zcode/skills/`；模块专属行为在 docs/ARCHITECTURE.md 对应节）。每条 = 规则 + 半句为什么 + 实锤锚点。

## 1. .NET 语义

- **.NET 10 起 Dispose 后的 CTS `Cancel()` 是 no-op，不抛 ObjectDisposedException**（变异实验实锤：对已释放实例直接 Cancel 的"崩溃"测试击不杀）——"已释放实例悬挂"不再表现为崩溃但仍是悬挂；防御性 try/catch 的价值是语义显式化，别假设 ODE 会替你暴露 bug（先例：`FfmpegVideoBackdropPlayer._cts` 摘除）。
- **.NET 10 悬空符号链接三语义（2026-09-25 /tmp 探针实锤，Unix）+ 文件系统防护必须枚举目标全部既有形态**：①`File.Exists(悬空)=true`（跟随语义失灵——UmuPrefix 曾因此漏判悬空 pfx 链接致启动永久失败，F21）；②`File.GetAttributes(悬空)` 不抛、返回链接自身属性（`ReparsePoint=true`）；③`Directory.Delete(悬空)` 抛 `DirectoryNotFoundException`（**Unix** 删链接须用 `File.Delete`）。**Windows 删除语义相反**（2026-09-25 Windows CI 三用例实锤）：目录链接是 directory reparse point，`File.Delete`（DeleteFileW）对其报 `ERROR_ACCESS_DENIED`，删目录链接（含悬空）须用 `Directory.Delete`（RemoveDirectory 摘 reparse point 本身、不跟随目标——官方 reparse 删除语义；Windows 腿修复 bf577e6 已随 CI 双腿复跑确认，2026-09-26）。**Windows 经典语义不同：`File.Exists(悬空)=false`**——放在 `File.Exists` 门内的链接检查会被 Windows 悬空形态绕过（`PackageInstallerService.ExtractArchive` 的目标 reparse 检查因此必须无条件执行）。写解压沙箱/覆盖删除类防护时，目标位置的形态全集 = 不存在/真实文件/真实目录/工作链接/悬空链接，逐一建模；`FileUtilities.IsReparsePoint` 的 catch 靠 IOException 臂即可覆盖 FNFE/DNFE（二者均为其子类，官方继承链探针复核）。已知残留面（立案 artifacts/bugs.md）：**硬链接**无 ReparsePoint 标记、.NET 无可移植 link count API，`ExtractToFile(overwrite)` 经预埋硬链接可写穿同卷外部文件。
- **改带选项结构体的 API 必须逐字段核对默认值与旧行为的差异，并用全形态对照测试钉住**（实锤 `FileUtilities.TryDeleteDirectory`）：为加 `IgnoreInaccessible` 换 `EnumerationOptions` 默认构造，其默认 `AttributesToSkip=Hidden|System` 在 Linux 上把点前缀条目（`.installed.ok`/`.yagl-*` 等，.NET 标记为 Hidden）从枚举剔除——含点文件的目录删不净、`Directory.Delete` 失败，UmuPrefix 清理路径全线回归。修复 = `AttributesToSkip = FileAttributes.None`。凡"为加一个选项换了构造形态"的改动，同结构体其余字段逐一过一遍。
- **IDE0005（未使用 using）有两类与肉眼相左的特例**：仅提供扩展方法的 using 构建期**不报**但可能必需；反之为必需 using 但肉眼看似未用（cref/扩展方法解析）。结论：删 using 以"删后编译"为准，IDE0005 的沉默不是充分证据。
- **`Process.StandardOutput.ReadToEnd()`（同步）会一直阻塞到子进程关闭 stdout，排在其后的 `WaitForExit(timeout)` 永远执行不到**——超时保护形同死代码，子进程挂起即卡死调用线程（实锤：`Program.cs` 启动期的 xrdb/hyprctl 查询曾会卡死 Main）。带超时的输出读取必须先 `ReadToEndAsync()` 再 `Wait(timeout)`，超时 Kill。
- **Windows 的 `CreateProcess` 对裸命令名自动补 `.exe`，而 `File.Exists`/`IsExecutableFile` 不会**：预检 PATH 上的裸命令（如 "hpatchz"）必须补试 `name + ".exe"`（`HpatchzApplier.ResolvePatchTool`），否则 Windows 误报工具缺失。Linux 还额外要求执行位（`FileUtilities.IsExecutableFile` 已含）。
- **写 catch 前查官方异常表全列；加 catch 后必须推演"接住之后呢"**：①`Process.Kill(entireProcessTree:true)` 官方抛 Win32Exception（无法终止/正在终止）+ AggregateException（子树未全终止），不止 InvalidOperationException；②同族 TOCTOU 异常类不同——`FileInfo.Length` 对文件缺失抛 FNFE、`File.OpenRead` 对父目录缺失抛 DNFE，catch 收窄即漏；③**接住异常不是终点**——接住后的下游路径（挂死/泄漏/分类丢失）要重新推演，等待类调用一律有界（实锤 F22-2 补 catch 后引入"Kill 真失败 → `WaitForExitAsync(None)` 永久挂"新风险）。

## 2. 文件系统与跨平台

- **Windows 占用/只读语义是跨平台更新的头号杀手**（Linux `rename()`/`unlink()` 总能成功，问题只在 Windows 暴露）：被占用或只读的文件会让 `File.Move(overwrite:true)` 抛 IOException/UnauthorizedAccessException、让 `Directory.Delete(recursive:true)` 整体抛异常。统一防线：原子写 `FileUtilities.WriteAtomicAsync`（解除只读+重试一次）、目录树 `FileUtilities.TryDeleteDirectory`（能删多少删多少）、下载落盘 `HttpFileDownloader.ReplaceDestination`（单独分类报"目标被占用"，绝不落进网络错误重试——重下多少遍都不会好）、背景落盘 `GameBackdropService` 换时间戳备用名。新增删除/覆盖代码先想这层。
- **`ZipFile.ExtractToDirectory` 在 Unix 把含 `\` 的 zip 条目名当字面文件名**（dotnet/runtime#98247，未修复）：Windows 打包器产出的包会在 Linux 解成安装根目录下的平铺垃圾文件，且"更新成功"。凡解包不可信外部 zip 必须手动遍历 `ZipArchive` 归一条目名（`PackageInstallerService.ExtractArchive`，顺带做 `..`/盘符穿越校验）。
- **切 PATH 必须用 `Path.PathSeparator`，不能硬编码 `':'`**：Windows 上盘符 `C:` 会被切开，`SearchPath`/`FindOnPath` 返回缺盘符的相对根路径（CI Windows 腿红过）。同理，`GameLauncherService.ValidateCommand` 仅在 `pathValue is null && Windows` 时跳过裸命令预检——测试注入 `pathValue:""` 必须仍走 PATH 扫描，否则预检/错误覆盖层用例在 Windows 上会假绿成「已启动」。
- **Windows 路径/注册表相关逻辑（自启动等）注意跨平台分支**（`OperatingSystem.IsWindows()`）：Linux 走 XDG autostart、Proton 兼容。
- **测试里的路径断言两侧必须统一分隔符再比较**：期望值 `Path.Combine(...)` 在 Windows 产反斜杠、实际值常被归一成正斜杠，Linux CI 恰好两侧同斜杠掩盖问题（Windows CI 一次红 11 个）。两类实锤：①只归一实际值没归一期望值（`UmuComponentProvisionerTests`）；②期望值拼相对路径硬编码 `/` 而生产经 `GetFullPath` 产原生分隔符，或反之配置模板里的 `{installDir}/saves` 展开是**字面替换**不归一（`LaunchParameterRoundTripTests`）。比较前两侧都过 `Replace('\\', '/')`。

## 3. 进程与日志

- **SystemProcessRunner 即启即走 + 输出日志三坑**：① `BeginOutputReadLine` 事件会丢 stderr——`WaitForExit()` 只排空 stdout，必须自管 ReadLine 泵到 EOF；② `using var process` 在方法返回即 Dispose，会掐断管道，日志模式须泵收尾后再释放句柄；③ 进程可能在 `EnableRaisingEvents=true` 布防前退出，此时 Exited 永不触发，布防后要补查 `HasExited`。（Windows 上测试轮询读生产进程正在写的日志须 `FileShare.ReadWrite` 打开——见 skills avalonia-headless-testing §5。）

## 4. 研究与取证

- **协议/格式的行为结论只认一手源码，二手摘要必须标注未核实**（F24 两连错实锤）：WebSearch 摘要给的 Valve KeyValues 转义表是错的，拉 `tier1/utlbuffer.cpp` raw 源码实证的真表与之不符（未知转义 = NUL+保留后续字符）。解析器/协议逆向类结论：摘要 → 源码溯源是硬门槛；注释里引用外部行为必须写明出处层级（"源码实证" vs "摘要未核"）。
