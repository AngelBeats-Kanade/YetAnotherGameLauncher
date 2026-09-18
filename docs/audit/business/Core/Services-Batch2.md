# Core/Services 逐行审计：GameInstallService + 100% 覆盖文件批（2026-09-19）

## GameInstallService.cs（219 行）—— ✅ 全绿 / ⚠️ 2 行未覆盖

| 行 | 判定 | 论证 | 覆盖 |
|---|---|---|---|
| 30-31 PreservedEntries | ✅ | 保留名单与 AGENTS.md 记载一致（.yagl/Saved/launcherDownloadConfig.json/compatdata）；compatdata 纵深防御注释自证 | NeverDeletes 测试 |
| 38-86 SyncAsync | ✅ | 快速校验→并行补齐→MD5 事后校验→按结论补一轮→复验失败抛可读异常→清理→Done；每一步都有专项测试（SameSizeCorruption/PostVerificationFailure/ReportsDoneProgress） | 已覆盖 |
| 89-142 DownloadBatchAsync | ✅ | 空批次早退；Parallel.ForEachAsync 限并发；EnsureDownloadUrl 纵深防御（MissingUrl 测试）；ResolveSafe 收容；进度 lock 串行化（高频回调不打散进度条的注释自证） | 已覆盖 |
| 153-210 CleanupStaleFiles | ✅ 逻辑 | 大小写不敏感比较系有意（少删不误删，注释自证）；ReparsePoint 不穿透（链接搬盘手法防线，专项测试）；IgnoreInaccessible 已在文档注明；单文件删除失败跳过（只读目录测试） | **未覆盖 157-158**（installDir 不存在早退——全量同步前目录必然被下载步骤创建；理论可达：清单为空 + 目录不存在。Phase 4：一行用例） |

## 100% 行覆盖文件批（逐文件通读，无未覆盖行）

| 文件 | 行数 | 判定 | 要点论证 |
|---|---:|---|---|
| GameUpdateService.cs | 198 | ✅ | 两段式预下载（staged manifest 原子性）、包式渠道整包语义、他游戏 state 不误判、UpdateException 分类——测试矩阵已列 Phase 1。增量失败回退全量、PredownloadPatchSourceVersions 精确匹配均直测 |
| ManifestVerifier.cs | 104 | ✅ | 快/全双档、../与绝对路径拒绝（平台扎根形式）、反斜杠归一、逐文件进度。VerifySafe 被 Install/Incremental 复用为收容点 |
| NetworkProxyManager.cs | 41 | ✅ | 三态互斥 + 非法地址回退 System，四用例全测 |
| PackageInstallerService.cs | 194 | ✅ | zip 沙箱四连回归（反斜杠/../目录/扎根目录）、暂存零重下、损坏暂存回退重下、只读目标解除——测试矩阵已列 Phase 1 |
| InstallPath.cs | 35 | ✅ | ~ 展开、绝对/相对解析，三直测 |
| PlatformInfoFactory.cs | 15 | ✅ | 唯一平台分支收口（与当前 OS 一致性直测） |
| LinuxAutostartService.cs / WindowsAutostartService.cs / AutostartContentTests 对应实现 | ~150 | ✅ | XDG 真实文件往返（home 注入）；Windows reg 命令形状断言（FakeProcessRunner） |

结论：本批无实锤 bug、无新增高危缺口；GameInstallService 2 行早退分支列 Phase 4。
