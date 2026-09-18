# Core/Services/Umu 逐行审计（7 文件，2026-09-19）

## UmuPrefix.cs（217 行）—— ✅ 逻辑 / ❗ 75 行未覆盖（本批最大缺口）

| 行 | 判定 | 论证 | 覆盖 |
|---|---|---|---|
| 17-42 Setup 主流程 | ✅ 逻辑 | 幂等契约（注释自证）；pfx.lock 独占（FileStream DeleteOnClose，无 unsafe）；pfx→. 符号链接；shadercache/gstreamer/tracked_files | 仅正路径覆盖（Setup_CreatesLayout）。**未覆盖 20-21**（空路径守卫）、46,48-49（EnsureUserExecute 的 Windows/缺文件 no-op——Windows CI 可达 + 一行用例）、52-53（执行位写入） |
| 56-88 AcquireLock | ✅ 逻辑 | 重试至超时抛 UpdateException（可操作消息）；FileShare.None 独占 | **未覆盖 77-87**（锁竞争重试/超时——Phase 4：先持锁再 Acquire 同路径，断言 UpdateException） |
| 90-124 EnsurePfxSymlink | ✅ 逻辑 | 真实目录保留（旧版/自建）；正确目标幂等返回；错误目标删重建；pfx 为文件则删；无 symlink 权限退化真实目录（注释自证） | **未覆盖 92-123 全部分支**（四种既存形态 × 幂等/修复矩阵）。Phase 4：幂等矩阵用例组（二次 Setup/真目录/错误链接/文件占位） |
| 126-153 SetupUserLinks | ✅ 逻辑 | drive_c 未 wineboot 早退；四分支用户互链矩阵（注释与上游 umu setup_pfx 对齐） | **未覆盖 135-152 全部**（现有测试的 prefix 无 drive_c）。Phase 4：构造 drive_c 后四分支矩阵 |
| 155-216 辅助 | ✅ 逻辑 | 链接探测/目标解析/尽力建链/删除 | 未覆盖为上述分支的组成行 |

## NativeUmuLauncher.cs（260 行）—— ✅ 逻辑 / ⚠️ 47 行未覆盖

| 行 | 判定 | 论证 | 覆盖 |
|---|---|---|---|
| 31-71 ResolveComponentsAsync | ✅ 逻辑 | provisioner 有无双模；Runtime 缺失分类 UmuRuntimeMissing | **未覆盖 45-50**（无 provisioner 且本地无 Proton → RuntimeMissing）、**63-66**（无 provisioner 且 runtime 未装）。Phase 4：两直调用例 |
| 74-146 BuildPlan | ✅ 逻辑 | EnsureLinux 门；exe 归一存在性；prefix 失败归类；PROTONPATH 代号不覆盖已解析绝对路径（路由测试断言）；extra env {exe}/{installDir} 展开 | **未覆盖 90-93**（exe 缺失→ExecutableMissing）、**103-108**（prefix 失败→PrefixCreateFailed。Phase 4：以文件占位 prefix 路径构造失败） |
| 148-253 LaunchAsync + 日志/进度 | ✅ 逻辑 | 组件解析→BuildPlan→即启即走；日志路径 | Linux 腿经路由测试覆盖主干；**未覆盖 185-191,209-253**（进度回调装配与无 provisioner 分支组合）。Phase 4：直调 LaunchAsync 一例 |
| 226-233 CreateElevatedStartInfo（文件内其余） | — | （在 SystemProcessRunner.md 已审） | — |

## VdfMiniParser.cs（158 行）—— ✅ 逻辑 / ⚠️ 22 行未覆盖

手写 Valve VDF 解析器（token 流 + 花括号栈）。正路径四字段解析有直测；未覆盖 22 行 = 转义引号、嵌套层级容错、注释/空白边界与畸形输入早退分支。**Phase 4**：畸形 VDF（未闭合括号/意外 token/转义引号）用例组。解析器是外部数据攻击面，优先级较高。

## ToolManifest.cs（101 行）—— ✅ 逻辑 / ⚠️ 13 行未覆盖

toolmanifest.vdf + compatibilitytool.vdf 双文件加载、host runtime 特例、入口命令构造。未覆盖 = compatibilitytool 缺失/display_name 缺省/host 分支（19,33-35,63,81-95）。Phase 4：三小用例。

## UmuEnvironment.cs（152 行）—— ✅ 逻辑 / ⚠️ 9 行未覆盖

UMU_ID/GAMEID/AppId-MD5 契约有专项测试。未覆盖 50-56（store 非空键）、110-115（STEAM_COMPAT_TOOL_PATHS 多段拼接分支）。Phase 4：store + tool paths 用例。

## SteamRuntimeCatalog.cs（65 行）—— ✅ 逻辑 / ⚠️ 2 行未覆盖

AppId→runtime 映射（sniper/steamrt4/arm64）与未知 AppId null 全测；未覆盖 30-31 为一个额外目录名变体。Phase 4 顺手。

## UmuPaths.cs（50 行）—— ✅ 逻辑 / ⚠️ 7 行未覆盖

路径拼装纯函数；未覆盖 39-49 = Proton 目录校验辅助（FindInstalledProton 的本地路径变体）。Phase 4：两行直调。

## 批次结论

无实锤 bug（全部为守护/分支未覆盖）。**UmuPrefix 75 行是全案第三大缺口且完全可测**——
Phase 4 的幂等矩阵用例组为最高优先补测项；VdfMiniParser 畸形输入组次之（外部数据攻击面）。
