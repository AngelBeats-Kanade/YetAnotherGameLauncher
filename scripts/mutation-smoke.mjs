#!/usr/bin/env node
// 每日变异冒烟批：把手工变异抽查流程脚本化——
// 每个变异 = 单点语义破坏 → 构建测试工程 → 定向运行守卫测试 → 必须变红（击杀）→ git 还原。
// 任何"存活"（测试照样绿）都说明守卫空心化，脚本非零退出。
//
// harness 教训（必须遵守）：
// 1. 变异后必须构建**测试工程**——测试 bin 持有自己的依赖副本，只构建 src 项目不生效；
// 2. 测试数据必须能区分变异前后（M8 教训）；
// 3. 全部结束后还原源码并重建，保证工作树与 bin 干净。
//
// 用法：node scripts/mutation-smoke.mjs            # 全批
//       node scripts/mutation-smoke.mjs M1 M9      # 只跑指定 id（本地调试）

import { execFileSync, spawnSync } from 'node:child_process';
import { readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';

const root = execFileSync('git', ['rev-parse', '--show-toplevel'], { encoding: 'utf8' }).trim();

// 变异批（find 必须在目标文件中恰好出现一次；stale 即报错，防止字符串漂移后变异静默失效）
const MUTATIONS = [
  {
    id: 'M1-local-state-roundtrip',
    file: 'src/YetAnotherGameLauncher.Core/Services/LocalStateService.cs',
    find: 'state.GameId == gameId',
    replace: 'state.GameId != gameId',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.LocalStateServiceTests.SaveThenLoad_RoundTrips',
  },
  {
    id: 'M3-downloader-md5-verify',
    file: 'src/YetAnotherGameLauncher.Core/Services/HttpFileDownloader.cs',
    find: 'if (!string.Equals(actualMd5, expectedMd5, StringComparison.OrdinalIgnoreCase))',
    replace: 'if (!string.Equals(actualMd5, actualMd5, StringComparison.OrdinalIgnoreCase))',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.HttpFileDownloaderTests.DownloadFileAsync_Md5Mismatch_RetriesFromScratchThenThrows',
  },
  {
    id: 'M5-update-planner-incremental',
    file: 'src/YetAnotherGameLauncher.Core/Services/UpdatePlanner.cs',
    find: '&& availablePatchSourceVersions.Contains(localVersion, StringComparer.Ordinal);',
    replace: '&& !availablePatchSourceVersions.Contains(localVersion, StringComparer.Ordinal);',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.UpdatePlannerTests.Plan_LocalVersionMatchesPatch_Incremental',
  },
  {
    id: 'M4-backdrop-version-gate',
    file: 'src/YetAnotherGameLauncher.Core/Services/GameBackdropService.cs',
    find: '&& string.Equals(cached.Region, region, StringComparison.Ordinal)',
    replace: '&& !string.Equals(cached.Region, region, StringComparison.Ordinal)',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.GameBackdropServiceTests.Resolve_VersionUnchanged_SkipsResolverAndNetwork',
  },
  {
    id: 'M9-offline-version-chip',
    file: 'src/YetAnotherGameLauncher/ViewModels/GameItemViewModel.cs',
    find: 'SetVersionChip(state?.Version, latestVersion: null, hasUpdate: false);',
    replace: 'SetVersionChip(null, latestVersion: null, hasUpdate: false);',
    project: 'tests/YetAnotherGameLauncher.App.Tests/YetAnotherGameLauncher.App.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.App.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.App.Tests.dll',
    method: 'YetAnotherGameLauncher.AppTests.GameItemOfflineTests.RefreshAsync_Offline_InstalledGame_ShowsLocalChipAndKeepsState',
  },
  {
    // 2026-09-20 三审新增；2026-09-22 重定向：操作完成消息必须归属发起时的服务器。
    // 原规格指向的共享写点在 2b053c0 重构中被抽进 RunUpdateAsync 骨架，预下载测试
    // 实际驱动的是本处（PredownloadAsync 自己的服务期门）——规格随源码漂移即空心化
    id: 'M12-status-message-server-scope',
    file: 'src/YetAnotherGameLauncher/ViewModels/GameItemViewModel.cs',
    find: 'if (ReferenceEquals(originServer, SelectedServer))',
    replace: 'if (true)',
    project: 'tests/YetAnotherGameLauncher.App.Tests/YetAnotherGameLauncher.App.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.App.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.App.Tests.dll',
    method: 'YetAnotherGameLauncher.AppTests.GameItemRefreshRaceTests.Predownload_ResultFromOldServer_DoesNotOverwriteNewServerStatus',
  },
  {
    // 2026-09-22 测试审计新增：VDF 转义处理（toolmanifest 读取正确性）——
    // 关掉转义分支后带转义引号的值解析错误
    id: 'M13-vdf-escape-handling',
    file: 'src/YetAnotherGameLauncher.Core/Services/Umu/VdfMiniParser.cs',
    find: 'if (text[i] == \'\\\\\' && i + 1 < text.Length)',
    replace: 'if (false && text[i] == \'\\\\\' && i + 1 < text.Length)',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.Umu.VdfMiniParserTests.Parse_EscapedQuoteAndBackslash_UnescapeToLiteral',
  },
  {
    // 2026-09-22 测试审计新增：自启切换后的状态复核——写入"成功"但状态没变必须报 false，
    // 恒真化复核后组策略拦截场景被误报为成功
    id: 'M14-autostart-state-verify',
    file: 'src/YetAnotherGameLauncher/ViewModels/MainWindowViewModel.cs',
    find: 'return await _autostart.IsEnabledAsync() == enabled;',
    replace: 'return true;',
    project: 'tests/YetAnotherGameLauncher.App.Tests/YetAnotherGameLauncher.App.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.App.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.App.Tests.dll',
    method: 'YetAnotherGameLauncher.AppTests.MainWindowViewModelSettingsApiTests.SetAutostartAsync_StateMismatchAfterSet_ReturnsFalseSilently',
  },
  {
    // 2026-09-22 测试审计新增：协议参数空值回退（国服/国际服切换依赖）——
    // 回退值换成 key 后空白配置回退出错误端点
    id: 'M15-gryphline-option-fallback',
    file: 'src/YetAnotherGameLauncher.Channels.Hypergryph/GryphlineProtocol.cs',
    find: ': fallback;',
    replace: ': key;',
    project: 'tests/YetAnotherGameLauncher.Channels.Hypergryph.Tests/YetAnotherGameLauncher.Channels.Hypergryph.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Channels.Hypergryph.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Channels.Hypergryph.Tests.dll',
    method: 'YetAnotherGameLauncher.Channels.Hypergryph.Tests.GryphlineProtocolTests.OptionOrDefault_MissingOrWhitespace_FallsBack',
  },
  {
    // 2026-09-22 测试审计新增：RunUpdateAsync 通用骨架的服务期门（更新/校验/应用预下载
    // 共用）——M12 原规格曾指向此写点，2b053c0 重构后由本守卫测试驱动
    id: 'M16-update-skeleton-server-scope',
    file: 'src/YetAnotherGameLauncher/ViewModels/GameItemViewModel.cs',
    find: 'if (!string.IsNullOrEmpty(message) && ReferenceEquals(originServer, SelectedServer))',
    replace: 'if (!string.IsNullOrEmpty(message))',
    project: 'tests/YetAnotherGameLauncher.App.Tests/YetAnotherGameLauncher.App.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.App.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.App.Tests.dll',
    method: 'YetAnotherGameLauncher.AppTests.GameItemRefreshRaceTests.Update_ResultFromOldServer_DoesNotOverwriteNewServerStatus',
  },
  {
    // 2026-09-24 review 扩容（2026-10-02 规格更新：3c17c91 重写为两段式判定后旧 find 串
    // 漂移 stale，CI 连红两天）：取反 defer 门——逃逸链接反而进 deferred 列表，Linux 上
    // 盘符形态（C:/evil）逃过第二段边界判定被创建（悬空符号链接 File.Exists=true），
    // 守卫测试的盘符理论用例红；相对 ../../ 形态仍被第二段拦住（纵深冗余，红灯只经盘符口径）
    id: 'M17-tar-link-target-escape',
    file: 'src/YetAnotherGameLauncher/Services/UmuComponentProvisioner.cs',
    find: 'if (!IsLinkEscapingDestination(destinationDir, key, link))',
    replace: 'if (IsLinkEscapingDestination(destinationDir, key, link))',
    project: 'tests/YetAnotherGameLauncher.App.Tests/YetAnotherGameLauncher.App.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.App.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.App.Tests.dll',
    method: 'YetAnotherGameLauncher.AppTests.UmuComponentProvisionerTests.ExtractTarArchive_EscapingLinkTargets_AreSkipped',
  },
  {
    // 2026-09-24 review 扩容：目录预载依赖序破坏必须红（P0 守卫——avcodec 先于 swresample
    // 驻留正是真机 avformat DT_NEEDED 解析必败的形态，2026-09-24 readelf 实测）
    id: 'M18-dependency-order-swap',
    file: 'src/YetAnotherGameLauncher/Services/FfmpegLibraryResolver.cs',
    find: '["avutil", "swresample", "swscale", "avcodec", "avformat", "avfilter", "avdevice"]',
    replace: '["avutil", "avcodec", "swresample", "swscale", "avformat", "avfilter", "avdevice"] /* MUTATION */',
    project: 'tests/YetAnotherGameLauncher.App.Tests/YetAnotherGameLauncher.App.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.App.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.App.Tests.dll',
    method: 'YetAnotherGameLauncher.AppTests.FfmpegLibraryMajorTests.LibraryDependencyOrder_SatisfiesBtbnRuntimeDeps',
  },
  {
    // 2026-09-29 预载窗口批（F82/R-F82-2）：predownload 块无 cdnList 时回退 default 块——
    // 去掉回退后窗口期点「预下载」必抛 "cdnList has no usable node"（真机 3.7.0 实测形态）
    id: 'M19-kuro-predownload-cdn-fallback',
    file: 'src/YetAnotherGameLauncher.Channels.Kuro/KuroChannelApi.cs',
    find: 'RequireCdn(block, index.Default)',
    replace: 'RequireCdn(block)',
    project: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/YetAnotherGameLauncher.Channels.Kuro.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Channels.Kuro.Tests.dll',
    method: 'YetAnotherGameLauncher.Channels.Kuro.Tests.KuroChannelApiTests.GetIncrementalManifest_PredownloadBlockWithoutCdnList_FallsBackToDefaultCdn',
  },
  {
    // 2026-09-29 预载窗口批（F83/R-F82-2；2026-10-02 规格随 bb8c3dd 漂移更新）：无 fromFolder
    // 直下条目回退「列表第一个 fromFolder」——去掉后条目指向差分包目录（预载块形态下
    // resources/ 真机全 404）
    id: 'M20-kuro-incremental-resource-base',
    file: 'src/YetAnotherGameLauncher.Channels.Kuro/KuroChannelApi.cs',
    find: 'FirstFromFolder(directResources) ?? patchEntry.BaseUrl',
    replace: 'patchEntry.BaseUrl',
    project: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/YetAnotherGameLauncher.Channels.Kuro.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Channels.Kuro.Tests.dll',
    method: 'YetAnotherGameLauncher.Channels.Kuro.Tests.KuroChannelApiTests.GetIncrementalManifest_ResourceWithoutFromFolder_FallsBackToFirstFromFolder',
  },
  {
    // 2026-10-02 krpdiff 主修批（bb8c3dd）：resource[] 的 krpdiff 条目必须从 Files 过滤、
    // 只经 Groups 表达——去掉过滤后 krpdiff 被套 fromFolder 前缀 zip/（真机 38/38 全 404，
    // 用户报障的 404 URL 即此形态），且下载两遍、apply 时搬进游戏目录
    id: 'M21-kuro-krpdiff-excluded-from-files',
    file: 'src/YetAnotherGameLauncher.Channels.Kuro/KuroChannelApi.cs',
    find: 'var directResources = (patchIndexFile.Resource ?? [])\n                .Where(r => !IsDiffResource(r.Dest) && !tiersToCrop.Any(t => IsInTierDirectory(r.Dest, t)))\n                .ToList();',
    replace: 'var directResources = (patchIndexFile.Resource ?? [])\n                .Where(r => !tiersToCrop.Any(t => IsInTierDirectory(r.Dest, t)))\n                .ToList();',
    project: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/YetAnotherGameLauncher.Channels.Kuro.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Channels.Kuro.Tests.dll',
    method: 'YetAnotherGameLauncher.Channels.Kuro.Tests.KuroChannelApiTests.GetIncrementalManifest_KrpdiffEntries_ExcludedFromFilesAndBackfilledIntoGroups',
  },
  {
    // 2026-10-02 krpdiff 主修批（bb8c3dd）：组的 PatchSize/PatchMd5 从 resource[] 同名条目
    // 回填（真机组层无 size/md5 键）——去掉后下载校验与进度总量恒空/零
    id: 'M22-kuro-group-size-md5-backfill',
    file: 'src/YetAnotherGameLauncher.Channels.Kuro/KuroChannelApi.cs',
    find: 'diffEntry is { Size: > 0 } ? diffEntry.Size : group.Size',
    replace: 'group.Size',
    project: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/YetAnotherGameLauncher.Channels.Kuro.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Channels.Kuro.Tests.dll',
    method: 'YetAnotherGameLauncher.Channels.Kuro.Tests.KuroChannelApiTests.GetIncrementalManifest_KrpdiffEntries_ExcludedFromFilesAndBackfilledIntoGroups',
  },
  {
    // 2026-10-03 RF-E 修复批（095e498）：组级回退解析缝的版本守卫——服务器版本与请求版本
    // 不一致时必须拒绝解析（否则拿错版本内容打旧版 dst 校验值，报误导性 Checksum mismatch）
    id: 'M23-fallback-resolver-version-guard',
    file: 'src/YetAnotherGameLauncher.Core/Services/GameUpdateService.cs',
    find: 'if (!string.Equals(info.LatestVersion, version, StringComparison.Ordinal))',
    replace: 'if (string.Equals(info.LatestVersion, version, StringComparison.Ordinal))',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.GameUpdateServiceTests.ApplyPredownloadAsync_ServerAheadOfStagedVersion_FallbackExplainsVersionDrift',
  },
  {
    // 2026-10-03 RF-C 修复批（7430263）：deleteFiles 产物豁免——清单自相矛盾地点名本次更新
    // 产物时必须跳过（否则刚产出且校验通过的文件被删且无自愈路径）
    id: 'M24-deletefiles-produced-spare',
    file: 'src/YetAnotherGameLauncher.Core/Services/IncrementalUpdateService.cs',
    find: 'if (producedThisUpdate.Contains(canonical))',
    replace: 'if (producedThisUpdate.Count < 0)',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.IncrementalUpdateServiceTests.ApplyAsync_DeleteFilesListingFreshlyProducedFile_SparesIt',
  },
  {
    // 2026-10-03 hpatchz 失败回退批：补丁器执行失败（真机 109 事件形态）必须经解析缝直下该组
    // 产物自救——把回退调用整体替换回"直接抛执行失败"后守卫测试应变红（无解析缝的兼容分支不在此
    // 变异面）。find 跨 await 行与参数续行（只匹配续行会留下悬空调用头，变异后是编译错误非测试红）
    id: 'M25-hpatchz-failure-fallback',
    file: 'src/YetAnotherGameLauncher.Core/Services/IncrementalUpdateService.cs',
    find: 'await DownloadGroupOutputsDirectlyAsync(\n                        newDir, group, $"patch application failed: {ex.Message}", dstUrlResolver, progress, cancellationToken).ConfigureAwait(false);',
    replace: 'throw new UpdateException($"Patch application failed ({group.PatchFile}): {ex.Message}", ex);',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.IncrementalUpdateServiceTests.ApplyAsync_HpatchzFailure_WithResolver_FallsBackToDirectDownload',
  },
  {
    // 2026-10-17 终末地分卷解压批：分卷必须按序号升序拼接（响应顺序不可依赖）——
    // 排序反转后渠道守卫（Files 顺序 = 001..NNN）应变红。规则实现 2026-10-17 复审后
    // 迁到 Core 的 SplitVolumeNaming（渠道与安装器共用），变异点随之下移
    id: 'M26-split-volume-order',
    file: 'src/YetAnotherGameLauncher.Core/Utilities/SplitVolumeNaming.cs',
    find: 'volumes.Sort(static (left, right) => left.Number.CompareTo(right.Number));',
    replace: 'volumes.Sort(static (left, right) => right.Number.CompareTo(left.Number));',
    project: 'tests/YetAnotherGameLauncher.Channels.Hypergryph.Tests/YetAnotherGameLauncher.Channels.Hypergryph.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Channels.Hypergryph.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Channels.Hypergryph.Tests.dll',
    method: 'YetAnotherGameLauncher.Channels.Hypergryph.Tests.GryphlineChannelApiTests.GetManifest_SplitVolumePacks_FlagsManifestAndOrdersByVolumeNumber',
  },
  {
    // 2026-10-17 同批：分卷清单必须走拼接解压（逐卷当独立 zip 解压正是用户报障形态）——
    // 分支反转后安装守卫（分卷解压一次并落盘内容）应变红
    id: 'M27-split-volume-extract-branch',
    file: 'src/YetAnotherGameLauncher.Core/Services/PackageInstallerService.cs',
    find: 'if (archivesAreSplitVolumes)\n        {',
    replace: 'if (!archivesAreSplitVolumes)\n        {',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.PackageInstallerSplitVolumeTests.InstallAsync_SplitVolumes_ExtractsConcatenatedArchiveOnce',
  },
  {
    // 2026-10-17 同批：拼接流的定位语义（复用同卷句柄时位置必须回到拼接空间对应偏移）——
    // 相对定位后跨卷/回退读取取错数据，流守卫（内容逐字节相等）应变红
    id: 'M28-split-volume-stream-seek',
    file: 'src/YetAnotherGameLauncher.Core/Utilities/SplitVolumeStream.cs',
    find: 'volume.Seek(offsetInVolume, SeekOrigin.Begin);',
    replace: 'volume.Seek(offsetInVolume, SeekOrigin.Current);',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Utilities.SplitVolumeStreamTests.Seek_RepositionsAcrossVolumes_ForRandomAccessReads',
  },
  {
    // 2026-10-17 旧版残留清理批：解压产物的丢弃守卫——清单自相矛盾地点名本次产物时必须保留
    //（删掉刚解压的文件会让安装缺文件且无自愈路径，增量链 RF-C 同纪律）
    id: 'M29-obsolete-produced-spare',
    file: 'src/YetAnotherGameLauncher.Core/Services/PackageInstallerService.cs',
    find: '|| producedSet.Contains(normalized)',
    replace: '|| producedSet.Count < 0',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.PackageInstallerObsoleteFilesTests.InstallAsync_ObsoleteFileAlsoProducedByArchive_Spared',
  },
  {
    // 2026-10-17 同批：状态目录（.yagl）不得被废弃清单点名删除——去掉守卫后状态文件被删
    //（词法穿越/链接防线由 EnsureNoReparseWithin 独立兜底，故此处取状态目录守卫作变异点）
    id: 'M30-obsolete-state-dir-spared',
    file: 'src/YetAnotherGameLauncher.Core/Services/PackageInstallerService.cs',
    find: 'normalized.StartsWith(LocalStateService.StateDirName + "/", StringComparison.Ordinal)',
    replace: 'string.Empty.StartsWith(LocalStateService.StateDirName + "/", StringComparison.Ordinal)',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.PackageInstallerObsoleteFilesTests.InstallAsync_StateDirEntries_NotTouched',
  },
  {
    id: 'M35-delta-unusable-falls-back',
    file: 'src/YetAnotherGameLauncher.Core/Services/DeltaPackageApplier.cs',
    find: '                if (!MultiPartZipEntryReader.TryCopyContentTo(\n                        payload, source.PatchPath, plan.CdKey, destination, cancellationToken, logger))\n                {\n                    throw new DeltaPackageUnusableException(\n                        $"Delta payload entry is unreadable: {source.PatchPath}; falling back to the full update.");\n                }',
    replace: '                if (!MultiPartZipEntryReader.TryCopyContentTo(\n                        payload, source.PatchPath, plan.CdKey, destination, cancellationToken, logger))\n                {\n                    throw new UpdateException($"Delta payload entry is unreadable: {source.PatchPath}");\n                }',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.GameUpdateServiceTests.UpdateAsync_DeltaPayloadUnusable_FallsBackToFullPackage',
  },
  {
    // 2026-10-17 复审修复批：静态条目改写表被无视 → config.ini 直接覆盖游戏根（用户可见配置丢失）
    id: 'M36-static-entry-rewrite',
    file: 'src/YetAnotherGameLauncher.Core/Services/DeltaPackageApplier.cs',
    find: 'var targetRelative = plan.StaticEntryRewrites.TryGetValue(name, out var rewritten) ? rewritten : name;',
    replace: 'var targetRelative = name;',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.DeltaPackageApplierTests.ApplyAsync_ConfigIniStaticEntry_LandsAsConfigIniNew',
  },
  {
    // 2026-10-17 复审修复批：临时名叶子复核移除 → 既有符号链接/硬链接被写穿（F19/F43 同族）
    id: 'M37-delta-temp-leaf-links',
    file: 'src/YetAnotherGameLauncher.Core/Services/DeltaPackageApplier.cs',
    // 调用点 2026-10-17 第三轮复审后移入 try（解除失败走 IO 折算），缩进随之变化
    find: 'FileUtilities.RemoveLinkedLeaf(deltaPath, logger);\n            FileUtilities.RemoveLinkedLeaf(outputPath, logger);',
    replace: '// MUTATION: temp leaf link checks removed',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.DeltaPackageApplierTests.ApplyAsync_TempLeafIsHardLink_UnlinksBeforeWriting',
  },
  {
    // 2026-10-17 复审修复批：旧暂存清单的分卷复算短路 → 分卷暂存退回逐卷解压（用户报障形态）
    id: 'M38-stale-manifest-split-restore',
    file: 'src/YetAnotherGameLauncher.Core/Services/PackageInstallerService.cs',
    find: 'if (!manifest.EntriesAreArchives || manifest.ArchivesAreSplitVolumes || manifest.Files.Count < 2)',
    replace: 'if (!manifest.EntriesAreArchives || manifest.ArchivesAreSplitVolumes || manifest.Files.Count < 99999)',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.PackageInstallerSplitVolumeTests.ApplyPredownload_StaleManifestWithoutSplitFlag_RestoresSplitExtraction',
  },
  {
    // 2026-10-17 复审修复批：多候选选择退回"只看第一候选" → 白退整包
    id: 'M39-patch-candidate-selection',
    file: 'src/YetAnotherGameLauncher.Core/Services/DeltaPackageApplier.cs',
    find: 'foreach (var candidate in file.Patches)',
    replace: 'foreach (var candidate in file.Patches.Take(1))',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.DeltaPackageApplierTests.ApplyAsync_MultipleCandidates_UsesFirstUsableOne',
  },
  {
    // 2026-10-17 复审修复批：错位 206 的对齐守卫移除 → 错字节被当目标数据（F64 同族）
    id: 'M40-remote-range-alignment-guard',
    file: 'src/YetAnotherGameLauncher.Core/Utilities/RemoteZipEntryReader.cs',
    find: 'if (response.Status != HttpStatusCode.PartialContent || response.RangeFrom != offset)',
    replace: 'if (false)',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Utilities.RemoteZipEntryReaderTests.TryReadEntry_MisalignedRangeResponse_RefusedInsteadOfWrongBytes',
  },
  {
    // 2026-10-17 实机事故批：条目上限的**取值**就是回归类别（512 MiB 曾让真机更新中断在第 267 条）
    id: 'M41-entry-cap-value',
    file: 'src/YetAnotherGameLauncher.Core/Utilities/MultiPartZipEntryReader.cs',
    find: 'internal static long EffectiveMaxEntryBytes => MaxEntryBytesForTests ?? MaxEntryBytes;',
    replace: 'internal static long EffectiveMaxEntryBytes => MaxEntryBytesForTests ?? 512L * 1024 * 1024;',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Utilities.MultiPartZipEntryReaderTests.EffectiveMaxEntryBytes_IsAboveMeasuredVendorMaximum',
  },
  {
    // 2026-10-17 复审 P2-3 修复批：补丁工具失败必须折算"载荷不可用"→ 回退整包（不许重试恒失败）
    id: 'M42-patch-tool-failure-fallback',
    file: 'src/YetAnotherGameLauncher.Core/Services/DeltaPackageApplier.cs',
    find: 'await ApplyPatchFileAsync(deltaPath, basePath, outputPath, relative, cancellationToken).ConfigureAwait(false);',
    replace: 'await patchApplier.ApplyFileAsync(deltaPath, basePath, outputPath, cancellationToken).ConfigureAwait(false);',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.DeltaPackageApplierTests.ApplyAsync_PatchToolFailure_UnusableInsteadOfHardError',
  },
  {
    // 进程起不来（Win32Exception）折算供给侧 = 保留载荷、不回退整包（2026-10-18 三分类 catch 重组后）
    id: 'M43-patch-tool-start-failure-fallback',
    file: 'src/YetAnotherGameLauncher.Core/Services/DeltaPackageApplier.cs',
    find: '        catch (System.ComponentModel.Win32Exception ex)\n        {\n            // 补丁器实现直接抛 Win32Exception（未经 HpatchzApplier 折算）：进程起不来 = 工具/环境侧\n            throw new DeltaPackageSupplyException($"Patch tool failed to start for {relative}: {ex.Message}", ex);\n        }',
    replace: '',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.DeltaPackageApplierTests.ApplyAsync_PatchToolStartFailure_SupplyInsteadOfUnusable',
  },
  {
    // 2026-10-17 复审 P1 修复批：载荷条目是"本地头+密文"片段，密文里的 PK\x05\x06 不得被当作
    // 中央目录（旧行为：全域搜签名 → 随机命中 → 判不可读且无回退）
    id: 'M32-aes-fragment-before-cd',
    file: 'src/YetAnotherGameLauncher.Core/Utilities/WinZipAesReader.cs',
    // （变异必须能编译：改写片段守卫时要保留 entryName 非空流分析，故把签名条件并进守卫本身）
    find: 'if (entryName is null || EntryNameMatches(blob, entryName))',
    replace: 'if (entryName is null || (EntryNameMatches(blob, entryName) && blob.LastIndexOf("PK\\x05\\x06"u8) < 0))',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Utilities.WinZipAesReaderTests.TryReadEntry_CiphertextContainingEndOfCentralDirectorySignature_StillReads',
  },
  {
    // 2026-10-17 复审 P2-2 修复批：空转安装（本轮零产物）不得按废弃清单清理
    id: 'M33-empty-run-no-cleanup',
    file: 'src/YetAnotherGameLauncher.Core/Services/PackageInstallerService.cs',
    find: 'if (produced.Count == 0)',
    replace: 'if (produced.Count < 0)',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.PackageInstallerObsoleteFilesTests.InstallAsync_EmptyManifestWithObsoleteList_DoesNotDeleteAnything',
  },
  {
    // 2026-10-17 复审 P3-1 修复批：静态条目（dll 等）也要计入产物，废弃清单点名时不得删
    id: 'M34-static-entries-count-as-produced',
    file: 'src/YetAnotherGameLauncher.Core/Services/DeltaPackageApplier.cs',
    find: 'var produced = CopyStaticEntries(installDir, payload, plan, progress, cancellationToken);',
    replace: '_ = CopyStaticEntries(installDir, payload, plan, progress, cancellationToken);\n        var produced = new List<string>(plan.Files.Count);',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.DeltaPackageApplierTests.ApplyAsync_StaticEntryCoveredByObsoleteList_Spared',
  },
  {
    // 2026-10-17 同批：渠道的版本守卫（服务器已前移时不得把新版本的删除清单当旧过渡的依据）
    id: 'M31-obsolete-list-version-guard',
    file: 'src/YetAnotherGameLauncher.Channels.Hypergryph/GryphlineChannelApi.cs',
    // find 必须唯一：同一行在 GetDeltaPackageAsync 里也有一处（批 3 新增），
    // 故带上紧随其后的日志行锁定 GetObsoleteFilesAsync 的版本守卫（否则脚本判 stale 整批红）
    find: 'if (!string.Equals(response.Version, toVersion, StringComparison.Ordinal))\n            {\n                logger?.LogInformation(\n                    "Obsolete file list skipped: server serves {Served}, target was {Target}.", response.Version, toVersion);',
    replace: 'if (string.Equals(response.Version, toVersion, StringComparison.Ordinal))\n            {\n                logger?.LogInformation(\n                    "Obsolete file list skipped: server serves {Served}, target was {Target}.", response.Version, toVersion);',
    project: 'tests/YetAnotherGameLauncher.Channels.Hypergryph.Tests/YetAnotherGameLauncher.Channels.Hypergryph.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Channels.Hypergryph.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Channels.Hypergryph.Tests.dll',
    method: 'YetAnotherGameLauncher.Channels.Hypergryph.Tests.GryphlineChannelApiTests.GetObsoleteFiles_ServerVersionMovedPastTarget_ReturnsNull',
  },
  {
    // 2026-10-18 诱饵父进程桥：候选顺序（PlatformProcess → UnityCrashHandler64 → Endfield）是
    // 修复契约的一部分——顺序被破坏时挑选用例必须红
    id: 'M44-decoy-candidate-order',
    file: 'src/YetAnotherGameLauncher.Core/Services/Umu/DecoyParentBridge.cs',
    find: '["PlatformProcess.exe", "UnityCrashHandler64.exe", "Endfield.exe"];',
    replace: '["UnityCrashHandler64.exe", "PlatformProcess.exe", "Endfield.exe"];',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.Umu.DecoyParentBridgeTests.TryPrepare_PicksFirstExistingCandidateInOrder',
  },
  {
    // 2026-10-18 诱饵父进程桥：桥命令行里含空格的游戏参数必须加引号
    //（游戏侧按 Windows 命令行规则拆分；不加引号会被拆散）
    id: 'M45-bridge-commandline-quoting',
    file: 'src/YetAnotherGameLauncher.Core/Services/Umu/NativeUmuLauncher.cs',
    find: 'command += argument.Contains(\' \') ? $" \\"{argument}\\"" : $" {argument}";',
    replace: 'command += $" {argument}";',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.Umu.NativeUmuDecoyParentTests.LaunchAsync_DecoyParent_GameArgumentsRideInBridgeCommandLine',
  },
  {
    // 2026-10-18 复审加固：helper 环境变量名是 C↔C# 跨语言契约——重命名 C# 常量时
    // 源码锚点测试必须红（否则失败形态是 helper 退出 2、游戏不出现而 UI 报「已启动」）
    id: 'M46-bridge-env-contract-anchor',
    file: 'src/YetAnotherGameLauncher.Core/Services/Umu/DecoyParentBridge.cs',
    find: 'public const string DecoyVariable = "YAGL_BRIDGE_DECOY";',
    replace: 'public const string DecoyVariable = "YAGL_BRIDGE_DECOY_X";',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.Umu.DecoyParentBridgeTests.BridgeSource_MatchesManagedBridgeContract',
  },
  {
    id: 'M47-operation-progress-generation-gate',
    file: 'src/YetAnotherGameLauncher/Services/OperationProgress.cs',
    find: '        if (currentGeneration() == generation)',
    replace: '        if (generation >= -1 && currentGeneration() >= 0)',
    project: 'tests/YetAnotherGameLauncher.App.Tests/YetAnotherGameLauncher.App.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.App.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.App.Tests.dll',
    method: 'YetAnotherGameLauncher.AppTests.OperationProgressTests.Report_AfterGenerationBump_IsDropped',
  },
  {
    id: 'M48-delta-verify-only-content-md5',
    file: 'src/YetAnotherGameLauncher.Core/Services/DeltaPackageApplier.cs',
    find: '        if (!actual.Equals(file.Md5, StringComparison.OrdinalIgnoreCase))',
    replace: '        if (actual.Length == 0 && !actual.Equals(file.Md5, StringComparison.OrdinalIgnoreCase))',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.DeltaPackageApplierTests.ApplyAsync_VerifyOnlyContentMismatch_NotApplicable',
  },
  {
    id: 'M49-delta-free-space-precheck',
    file: 'src/YetAnotherGameLauncher.Core/Services/DeltaPackageApplier.cs',
    find: '        var alreadyAtTarget = EnsureApplicable(installDir, plan);\n        EnsureEnoughFreeSpace(installDir, payloadDir, plan);',
    replace: '        var alreadyAtTarget = EnsureApplicable(installDir, plan);\n        if (DateTime.MinValue == DateTime.MaxValue) { EnsureEnoughFreeSpace(installDir, payloadDir, plan); }',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.DeltaPackageApplierTests.ApplyAsync_NotEnoughFreeSpace_ActionableErrorBeforeDownload',
  },
  {
    id: 'M50-predownload-delta-branch',
    file: 'src/YetAnotherGameLauncher.Core/Services/GameUpdateService.cs',
    find: '        if (filePatchApplier is not null)\n        {\n            var deltaPlan = await channel.GetDeltaPackageAsync',
    replace: '        if (filePatchApplier is not null && DateTime.MinValue == DateTime.MaxValue)\n        {\n            var deltaPlan = await channel.GetDeltaPackageAsync',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.GameUpdateServiceTests.PredownloadAsync_PackageChannelWithDeltaPlan_StagesDeltaInsteadOfPackages',
  },
  {
    id: 'M51-delta-supply-catch',
    file: 'src/YetAnotherGameLauncher.Core/Services/DeltaPackageApplier.cs',
    find: '        catch (PatchToolUnavailableException ex)\n        {\n            throw new DeltaPackageSupplyException($"Patch tool unavailable for {relative}: {ex.Message}", ex);\n        }\n',
    replace: '',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.DeltaPackageApplierTests.ApplyAsync_PatchToolUnavailable_SupplyInsteadOfUnusable',
  },
  {
    id: 'M52-blind-registration-unverified-flag',
    file: 'src/YetAnotherGameLauncher/ViewModels/GameItemViewModel.cs',
    find: '                    Verified = false,',
    replace: '                    Verified = true,',
    project: 'tests/YetAnotherGameLauncher.App.Tests/YetAnotherGameLauncher.App.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.App.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.App.Tests.dll',
    method: 'YetAnotherGameLauncher.AppTests.GameItemActionsTests.RegisterVersion_OnPackageChannelDetected_RegistersWithoutDownload',
  },
  {
    id: 'M53-launch-button-disabled-while-busy',
    file: 'src/YetAnotherGameLauncher/Controls/DetailActionDock.axaml',
    find: '                        IsEnabled="{Binding CanClickLaunch}"\n',
    replace: '',
    project: 'tests/YetAnotherGameLauncher.App.Tests/YetAnotherGameLauncher.App.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.App.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.App.Tests.dll',
    method: 'YetAnotherGameLauncher.UiTests.DetailActionDockHeadlessTests.UpdateInProgress_DisablesLaunchButton_AndReenablesAfterCompletion',
  },
  {
    id: 'M54-aes-ctr-chunk-continuation',
    file: 'src/YetAnotherGameLauncher.Core/Utilities/WinZipAesReader.cs',
    find: '                var offsetInBlock = 16 - _available;\n',
    replace: '                var offsetInBlock = 0;\n',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Utilities.WinZipAesReaderTests.AesCtrDecryptor_ChunkedCalls_MatchOneShotAndReference',
  },
  {
    // 新守卫的安全性挂在清单 version 逐字符兜底上：窗口形态下问线上版本必须仍 null（2026-10-19）
    id: 'M55-delta-manifest-version-backstop',
    file: 'src/YetAnotherGameLauncher.Channels.Hypergryph/GryphlineChannelApi.cs',
    find: '            if (manifest?.Files is not { Count: > 0 } manifestFiles\n                || string.IsNullOrEmpty(manifest.VfsBasePath)\n                || !string.Equals(manifest.Version, toVersion, StringComparison.Ordinal))',
    replace: '            if (manifest?.Files is not { Count: > 0 } manifestFiles\n                || string.IsNullOrEmpty(manifest.VfsBasePath))',
    project: 'tests/YetAnotherGameLauncher.Channels.Hypergryph.Tests/YetAnotherGameLauncher.Channels.Hypergryph.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Channels.Hypergryph.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Channels.Hypergryph.Tests.dll',
    method: 'YetAnotherGameLauncher.Channels.Hypergryph.Tests.GryphlineChannelApiTests.GetDeltaPackage_PredownloadWindow_LiveVersionTargetStaysNull',
  },
  {
    // 2026-10-19 Windows 腿红修复批（复审 nit）：平台模拟缝不得回落当前平台分隔符（Windows 主机上
    // 模拟 POSIX 会把 '/' 路径比死）。变异取"恒 '/' 硬改 '\\'"——Ubuntu 腿即可击杀（实测 4 例）；
    // 历史缺陷形态（回落 Path.DirectorySeparatorChar）在 Linux 上等于 '/'，杀不掉，故不取该形态。
    id: 'M56-mount-boundary-separator',
    file: 'src/YetAnotherGameLauncher.Core/Utilities/DiskSpace.cs',
    find: '            var separator = \'/\';',
    replace: '            var separator = \'\\\\\';',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Utilities.DiskSpaceTests.Probe_PicksLongestMountPrefix',
  },
  {
    // 2026-10-19 档位资源包：目标档位清单必须裁掉其它档位目录。不裁则选 UHD 也把 HD 条目算进目标
    // 清单——"只下所选档"语义失效（HD 45.7 GB 白下一遍），且档位目录内容随同步被改写。
    id: 'M57-kuro-tier-crop-other-dirs',
    file: 'src/YetAnotherGameLauncher.Channels.Kuro/KuroChannelApi.cs',
    find: 'baseEntries.Where(e => !ResourceTiers.All.Any(t => IsInTierDirectory(e.Dest, t))),',
    replace: 'baseEntries.Where(e => true),',
    project: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/YetAnotherGameLauncher.Channels.Kuro.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Channels.Kuro.Tests.dll',
    method: 'YetAnotherGameLauncher.Channels.Kuro.Tests.KuroResourcePackTests.GetManifest_TieredTier_CropsOtherTiersAndAppendsSelectedPack',
  },
  {
    // 2026-10-19 档位资源包（P0 形态）：同步收尾清理必须尊重清单的保留前缀。忽略它，未选中档位目录
    // 会被当游离文件删除——切档后旧档几十 GB 无确认即丢（删除只应经显式「清理其他档位」）。
    id: 'M58-preserved-paths-honored',
    file: 'src/YetAnotherGameLauncher.Core/Services/GameInstallService.cs',
    find: 'var preservedPrefixes = ManifestVerifier.NormalizePreservedPrefixes(manifest.PreservedPaths);',
    replace: 'var preservedPrefixes = ManifestVerifier.NormalizePreservedPrefixes([]);',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.ResourceTierUpdateTests.FullSync_PreservedTierDirectory_NotDeletedAsStale',
  },
  {
    // 2026-10-19 档位资源包：主按钮优先级——档位缺包必须压过"有更新"（点一次 = 更新 + 补包一体；
    // 反过来用户先更新、再被"下载资源包"二次打断）。
    id: 'M59-install-button-pack-priority',
    file: 'src/YetAnotherGameLauncher/ViewModels/GameItemViewModel.cs',
    find: ': NeedsResourcePack ? Loc["game_downloadResourcePack"]',
    replace: ': false ? Loc["game_downloadResourcePack"]',
    project: 'tests/YetAnotherGameLauncher.App.Tests/YetAnotherGameLauncher.App.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.App.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.App.Tests.dll',
    method: 'YetAnotherGameLauncher.AppTests.ResourceTierVmTests.Refresh_DiskTierDiffersFromSelection_ShowsDownloadPackButtonAndStatus',
  },
  {
    // 2026-10-19 review（阻塞项）：增量废弃清单必须按档位裁剪——原样透传会让 SD/UHD 更新删掉
    // HD 目录里清单点名的文件，与"切档不删旧档、删除只经显式清理"冲突（几十 GB 用户数据）。
    id: 'M60-kuro-tier-deletefiles-crop',
    file: 'src/YetAnotherGameLauncher.Channels.Kuro/KuroChannelApi.cs',
    find: '                if (tiersToCrop.Any(t => IsInTierDirectory(path, t)))\n                {\n                    continue;\n                }',
    replace: '                if (DateTime.MinValue == DateTime.MaxValue)\n                {\n                    continue;\n                }',
    project: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/YetAnotherGameLauncher.Channels.Kuro.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Channels.Kuro.Tests.dll',
    method: 'YetAnotherGameLauncher.Channels.Kuro.Tests.KuroResourcePackTests.GetIncrementalManifest_SdTier_CropsOtherTierFilesAndGroups',
  },
  {
    // 2026-10-19 review（阻塞项）消费端纵深：废弃清单点名保留档位目录内的内容必须豁免——
    // 渠道漏裁时这里兜底，删掉即"档位内容被隐式删除"且用户无确认机会。
    id: 'M61-preserved-prefix-delete-guard',
    file: 'src/YetAnotherGameLauncher.Core/Services/IncrementalUpdateService.cs',
    // 2026-10-26 归一串重构后锚点由 relative 改 canonical。替换形态保持原作者写法：`false && <非空常量>`
    // 不是常量表达式、不触发 CS0162（实测；只有恒定 `if (false){…}` 才触发）。
    find: 'if (ManifestVerifier.IsUnderPreservedPrefix(canonical, preservedPrefixes))',
    replace: 'if (false && ManifestVerifier.IsUnderPreservedPrefix(canonical, preservedPrefixes))',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.IncrementalUpdateServiceTests.ApplyAsync_DeleteFilesUnderPreservedPrefix_SparesThem',
  },
  {
    // 2026-10-19 review（阻塞项）：续传必须按标记种类派回被暂停的动作——预下载暂停后改走更新链
    // 会绕过"先暂存、确认后应用"的两段契约（还可能直接应用/跳版本）。
    id: 'M62-resume-kind-dispatch',
    file: 'src/YetAnotherGameLauncher/ViewModels/GameItemViewModel.cs',
    find: 'if (session.Kind == DownloadSessionKind.Predownload)',
    replace: 'if (false && session.Kind == DownloadSessionKind.Predownload)',
    project: 'tests/YetAnotherGameLauncher.App.Tests/YetAnotherGameLauncher.App.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.App.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.App.Tests.dll',
    method: 'YetAnotherGameLauncher.AppTests.ResourceTierVmTests.Resume_PausedPredownload_RunsPredownloadChainInsteadOfUpdate',
  },
  {
    // 2026-10-19 二审 N2：包式（归档）应用链的载荷与暂存清单必须留到调用方提交点——
    // 在应用方法内就删（旧行为）会让"解压完到状态落盘之间"掉线的续跑整包重下数十 GB
    //（解压是同步长任务，令牌只在之后的写盘 await 生效，该窗口完全可达）。
    id: 'M63-package-apply-keeps-payload',
    file: 'src/YetAnotherGameLauncher.Core/Services/PackageInstallerService.cs',
    find: 'logger?.LogInformation("Predownload applied: {Version} ({Count} archives)", manifest.Version, total);',
    replace: 'IncrementalUpdateService.ClearStaging(installDir, logger);\n        logger?.LogInformation("Predownload applied: {Version} ({Count} archives)", manifest.Version, total);',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.PackageInstallerServiceTests.ApplyPredownloadAsync_KeepsStagingAndPayloadForResume',
  },
  {
    // 2026-10-19 二审 N1：预下载不可继续（窗口关闭）时必须清掉那条标记，否则主按钮永远停在
    // 「继续下载」且每次点击都在同一处失败、UI 无任何出路（种类/归属双重核对，其它标记不动）。
    id: 'M64-stale-predownload-marker-cleared',
    file: 'src/YetAnotherGameLauncher.Core/Services/GameUpdateService.cs',
    find: 'ClearStalePredownloadMarker(installDir, game.Id, server.Id);\n            throw new UpdateException("No predownload is currently open.");',
    replace: 'throw new UpdateException("No predownload is currently open.");',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.GameUpdateServiceTests.PredownloadAsync_WindowClosed_ClearsStalePredownloadMarker',
  },
  {
    // 2026-10-19 三审：应用器不得自删 v2 载荷（.yagl/delta）——清理属于调用方提交点。
    // 把删除加回应用方法内（旧行为）即击杀"载荷保留"断言。
    id: 'M65-delta-apply-keeps-payload',
    file: 'src/YetAnotherGameLauncher.Core/Services/DeltaPackageApplier.cs',
    find: 'payload.Dispose();\n\n        logger?.LogInformation(',
    replace: 'payload.Dispose();\n        FileUtilities.TryDeleteDirectory(PayloadDir(installDir), logger);\n        logger?.LogInformation(',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.DeltaPackageApplierTests.ApplyAsync_EnoughFreeSpace_ProceedsNormally',
  },
  {
    // 2026-10-19 三审：续跑必须按"已到位"跳过（差分节点以基 md5 为判据，重走会被判不适用）。
    // 摘掉登记即击杀"应用完成后再跑零下载零补丁"的用例。
    id: 'M66-delta-resume-skips-already-applied',
    file: 'src/YetAnotherGameLauncher.Core/Services/DeltaPackageApplier.cs',
    find: 'alreadyAtTarget.Add(relative);',
    replace: '// 变异：不再登记已到位节点。',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.DeltaPackageApplierTests.ApplyAsync_SecondRunAfterCompletedApply_SkipsAlreadyAppliedWithoutDownload',
  },
  {
    // 2026-10-19 四审：直链（UpdateAsync）提交点必须清 v2 载荷——应用器已不自删，摘掉这条
    // 清理没有任何其它用例会红（8.72 GiB 静默留盘的回归），守卫落在直链 happy path 用例。
    id: 'M67-direct-chain-clears-delta-at-commit',
    file: 'src/YetAnotherGameLauncher.Core/Services/GameUpdateService.cs',
    find: '            ClearDeltaPayload(installDir);\n        }',
    replace: '            // 变异：提交点不再清 delta 载荷。\n        }',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.GameUpdateServiceTests.UpdateAsync_DeltaPlanAvailable_AppliesDeltaInsteadOfFullPackage',
  },
  {
    // 2026-10-19 四审：已到位节点必须在基文件检查之前放行（应用完成后官方废弃清理会删旧基
    // 文件）。摘掉早退的 continue 即击杀"基文件已删的续跑"用例。
    id: 'M68-already-at-target-precedes-base-check',
    file: 'src/YetAnotherGameLauncher.Core/Services/DeltaPackageApplier.cs',
    find: '                alreadyAtTarget.Add(relative);\n                continue;',
    replace: '                alreadyAtTarget.Add(relative);',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.DeltaPackageApplierTests.ApplyAsync_SecondRunAfterCompletedApply_SkipsAlreadyAppliedWithoutDownload',
  },
  {
    // 2026-10-20 诱饵父进程桥：helper 必须是 GUI 子系统 PE——console 子系统会在无终端的 Wine 链上
    // 开 conhost 黑窗（用户报障的原始缺陷）。改回 Exe 即击杀嵌入资源的结构断言。
    id: 'M69-bridge-gui-subsystem',
    file: 'tools/wine-parent-bridge/WineParentBridge.csproj',
    find: '<OutputType>WinExe</OutputType>',
    replace: '<OutputType>Exe</OutputType>',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.Umu.DecoyParentBridgeTests.EmbeddedHelper_IsPinnedPeBinary',
  },
  {
    // 2026-10-20 诱饵父进程桥：桥目录迁移的旧目录清理是"安装目录只读"契约的一部分——
    // 摘掉清理即击杀迁移用例（旧 `.yagl/bridge` 残留会长期占位且与新版行为矛盾）。
    id: 'M70-bridge-legacy-cleanup',
    file: 'src/YetAnotherGameLauncher.Core/Services/Umu/DecoyParentBridge.cs',
    find: '            TryRemoveLegacyBridge(installDir, bridgeRoot);',
    replace: '            // TryRemoveLegacyBridge(installDir, bridgeRoot);',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.Umu.DecoyParentBridgeTests.TryPrepare_RemovesLegacyBridgeDirectory',
  },
  {
    // 2026-10-20 诱饵父进程桥：诱饵必须落在桥上（应用数据目录），不得写回游戏安装目录。
    id: 'M71-bridge-decoy-outside-install',
    file: 'src/YetAnotherGameLauncher.Core/Services/Umu/DecoyParentBridge.cs',
    find: 'var decoyPath = Path.Combine(bridgeRoot, DecoyFileName);',
    replace: 'var decoyPath = Path.Combine(installDir, DecoyFileName);',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.Umu.DecoyParentBridgeTests.TryPrepare_PicksFirstExistingCandidateInOrder',
  },
  {
    // 2026-10-20 复审：Windows 上已挂起的诱饵会锁住 Games.exe，内容一致时必须跳过覆盖——
    // 改回无条件复制即击杀"目标被锁仍能准备"用例（第二次启动 / 孤儿诱饵场景）。
    id: 'M72-bridge-decoy-skip-identical-copy',
    file: 'src/YetAnotherGameLauncher.Core/Services/Umu/DecoyParentBridge.cs',
    find: 'if (!HasSameContent(decoyPath, source))',
    replace: 'if (true)',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.Umu.DecoyParentBridgeTests.TryPrepare_KeepsMatchingDecoyWhenDestinationIsLocked',
  },
  {
    // 2026-10-20 复审：READY 是"游戏进程已创建"的唯一成功信号；短路 READY 分支即击杀
    // 状态读取用例（launcher 会等到超时）。
    id: 'M73-bridge-status-ready',
    file: 'src/YetAnotherGameLauncher.Core/Services/Umu/BridgeLaunchStatus.cs',
    find: 'if (text.StartsWith(DecoyParentBridge.ReadyStatus, StringComparison.Ordinal))',
    replace: 'if (text.StartsWith(DecoyParentBridge.ReadyStatus, StringComparison.Ordinal) && !text.StartsWith(DecoyParentBridge.ReadyStatus, StringComparison.Ordinal))',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.Umu.BridgeLaunchStatusTests.WaitAsync_Ready_ReturnsReadyAndCleansStatusFile',
  },
  {
    // 2026-10-20 复审：ERROR 状态必须映射为失败；短路该分支即击杀错误状态用例。
    id: 'M74-bridge-status-error',
    file: 'src/YetAnotherGameLauncher.Core/Services/Umu/BridgeLaunchStatus.cs',
    find: 'if (text.StartsWith(DecoyParentBridge.ErrorStatusPrefix, StringComparison.Ordinal))',
    replace: 'if (text.StartsWith(DecoyParentBridge.ErrorStatusPrefix, StringComparison.Ordinal) && !text.StartsWith(DecoyParentBridge.ErrorStatusPrefix, StringComparison.Ordinal))',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.Umu.BridgeLaunchStatusTests.WaitAsync_Error_ReturnsExitCodeAndMessage',
  },
  {
    // 2026-10-20 复审：Windows 直启只有 READY 才算成功；反转判断即击杀 ERROR 状态映射用例。
    id: 'M75-bridge-windows-ready-gate',
    file: 'src/YetAnotherGameLauncher.Core/Services/GameLauncherService.cs',
    find: 'if (status.State == BridgeLaunchState.Ready)',
    replace: 'if (status.State != BridgeLaunchState.Ready)',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.GameLauncherServiceTests.LaunchAsync_DecoyParent_OnWindows_HelperError_ThrowsActionableError',
  },
  {
    // 2026-10-20 复审：Linux 原生链同样必须把 ERROR 状态转为可操作错误。反转判断即击杀用例。
    id: 'M76-bridge-linux-error-gate',
    file: 'src/YetAnotherGameLauncher.Core/Services/Umu/NativeUmuLauncher.cs',
    find: 'if (status.State == BridgeLaunchState.Failed)',
    replace: 'if (status.State != BridgeLaunchState.Failed)',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.Umu.NativeUmuDecoyParentTests.LaunchAsync_DecoyParent_HelperError_ThrowsActionableError',
  },
  {
    // 2026-10-20 复审 nit：状态文件在取消时也必须清理；摘掉 WaitAsync 的 finally 清理即击杀取消用例
    id: 'M77-bridge-status-cleanup-on-cancel',
    file: 'src/YetAnotherGameLauncher.Core/Services/Umu/BridgeLaunchStatus.cs',
    find: '        finally\n        {\n            TryDelete(statusPath);\n        }',
    replace: '        finally\n        {\n        }',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.Umu.BridgeLaunchStatusTests.WaitAsync_Cancellation_CleansStatusFile',
  },
  {
    // 2026-10-20 复审 nit：崩溃/取消遗留的 status-*.txt 按时间阈值清理；摘掉清理即击杀用例
    id: 'M78-bridge-stale-status-cleanup',
    file: 'src/YetAnotherGameLauncher.Core/Services/Umu/DecoyParentBridge.cs',
    find: '            TryRemoveStaleStatusFiles(bridgeRoot);',
    replace: '            // TryRemoveStaleStatusFiles(bridgeRoot);',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.Umu.DecoyParentBridgeTests.TryPrepare_RemovesStaleStatusFilesButKeepsFreshOnes',
  },
  {
    // 2026-10-20 复审 nit：Windows 进程启动失败时清理本次状态占位文件；摘掉即击杀清理用例
    id: 'M79-bridge-windows-start-failure-cleanup',
    file: 'src/YetAnotherGameLauncher.Core/Services/GameLauncherService.cs',
    find: '        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)\n        {\n            BridgeLaunchStatus.TryDelete(statusPath);\n            logger?.LogWarning(ex, "Decoy bridge start failed: {File}", preparation.HelperPath);',
    replace: '        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)\n        {\n            logger?.LogWarning(ex, "Decoy bridge start failed: {File}", preparation.HelperPath);',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.GameLauncherServiceTests.LaunchAsync_DecoyParent_OnWindows_ProcessStartFailure_CleansStatusFile',
  },
  {
    // 2026-10-20 复审 nit：Linux 在 BuildPlan 失败（helper 尚未启动）时清理状态占位文件；摘掉即击杀用例
    id: 'M80-bridge-linux-buildplan-failure-cleanup',
    file: 'src/YetAnotherGameLauncher.Core/Services/Umu/NativeUmuLauncher.cs',
    find: '        catch\n        {\n            // BuildPlan 失败（exe 缺失/配置错误）时 helper 尚未启动，清掉刚创建的占位文件\n            BridgeLaunchStatus.TryDelete(statusPath);\n            throw;\n        }',
    replace: '        catch\n        {\n            throw;\n        }',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.Umu.NativeUmuDecoyParentTests.LaunchAsync_DecoyParent_BuildPlanFailure_CleansStatusFile',
  },
  {
    // 2026-10-20 复审（安全）：game.Id 直接拼桥目录；结尾点会被 Windows 规范化（game. 与 game
    // 同目录），"."/".." 也以点结尾。摘掉尾点拒绝即击杀回归用例。
    id: 'M81-catalog-id-trailing-dot',
    file: 'src/YetAnotherGameLauncher.Core/Services/GameCatalogService.cs',
    find: "        && !id.EndsWith('.')",
    replace: '        && true',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.GameCatalogServiceTests.LoadAsync_GameIdDotSegment_ThrowsValidationException',
  },
  {
    // 2026-10-21：容器拉起 helper 的阶段必须按启动上限计时（含 Proton/umu 冷启动），
    // 与 READY 上限分开；退回单一超时即击杀「helper 迟到但最终 READY」用例。
    id: 'M82-bridge-status-startup-window',
    file: 'src/YetAnotherGameLauncher.Core/Services/Umu/BridgeLaunchStatus.cs',
    find: 'var limit = helperStarted ? timeout : startupLimit;',
    replace: 'var limit = timeout;',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.Umu.BridgeLaunchStatusTests.WaitAsync_HelperStartsAfterReadyTimeout_StillWaitsForStartupWindow',
  },
  {
    id: 'M83-process-runner-keeps-inherited-diagnostic-ports',
    file: 'src/YetAnotherGameLauncher.Core/Services/SystemProcessRunner.cs',
    find: '"DOTNET_DiagnosticPorts",',
    replace: '"DOTNET_DiagnosticPorts_DISABLED",',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.SystemProcessRunnerHostEnvironmentTests.RunAsync_WaitForExit_StripsInheritedDotnetHostInstrumentation',
  },
  {
    // 2026-10-22：省略 startupTimeout 时必须用调用方 timeout，而不是静默回退 10 s；
    // 仅断言 TimedOut 会被 10 s 默认值蒙混通过，耗时限界负责击杀。
    id: 'M84-bridge-startup-omitted-default',
    file: 'src/YetAnotherGameLauncher.Core/Services/Umu/BridgeLaunchStatus.cs',
    find: 'var startupLimit = startupTimeout ?? timeout;',
    replace: 'var startupLimit = startupTimeout ?? DefaultStartupTimeout;',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.Umu.BridgeLaunchStatusTests.WaitAsync_StartupTimeoutOmitted_StartupPhaseUsesTimeout',
  },
  {
    // 2026-10-22：STARTING 后必须改用 READY 上限；沿用 startupLimit 会让不等预算的调用方
    // 多等一整段启动预算，耗时限界负责击杀。
    id: 'M85-bridge-ready-uses-startup-budget',
    file: 'src/YetAnotherGameLauncher.Core/Services/Umu/BridgeLaunchStatus.cs',
    find: 'var limit = helperStarted ? timeout : startupLimit;',
    replace: 'var limit = startupLimit;',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.Umu.BridgeLaunchStatusTests.WaitAsync_HelperStarted_ReadyPhaseUsesConfiguredTimeout',
  },
  {
    // 2026-10-22：生产 helper 走 fire-and-forget + 输出日志；剥离若被限定在 WaitForExit
    // 分支，只有该路径的守卫能发现。
    id: 'M86-process-runner-strip-only-wait-for-exit',
    file: 'src/YetAnotherGameLauncher.Core/Services/SystemProcessRunner.cs',
    find: 'if (startInfo.Environment.Remove(variable))',
    replace: 'if (waitForExit && startInfo.Environment.Remove(variable))',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.SystemProcessRunnerHostEnvironmentTests.RunAsync_FireAndForgetWithLog_StripsInheritedDotnetHostInstrumentation',
  },
  {
    // 2026-10-22：托管 PROTON_LOG_DIR 必须被自定义 logDirectory 接管；退回应用日志目录
    // 会让原生链的 Proton 日志与 launch 日志分叉。
    id: 'M87-umu-managed-proton-log-dir',
    file: 'src/YetAnotherGameLauncher.Core/Services/Umu/NativeUmuLauncher.cs',
    find: 'environment["PROTON_LOG_DIR"] = _logDirectory;',
    replace: 'environment["PROTON_LOG_DIR"] = AppPaths.LogDirectory;',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.Umu.NativeUmuLauncherLaunchTests.BuildPlan_ProtonLogDirectoryUsesLauncherLogDirectory',
  },
  {
    // 2026-10-22：用户手写 PROTON_LOG_DIR 必须保留；无条件接管会吞用户目录。
    id: 'M88-umu-user-proton-log-dir',
    file: 'src/YetAnotherGameLauncher.Core/Services/Umu/NativeUmuLauncher.cs',
    find: 'if (!hasUserDirectory)',
    replace: 'if (true)',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.Umu.NativeUmuLauncherLaunchTests.BuildPlan_RespectsUserProtonLogDirectory',
  },
  {
    // 2026-10-22 复审补：M88 改指无条件接管（用户优先面）后，托管值排除项无人扰动。
    // 摘掉 AppPaths.LogDirectory 比对即把调用方注入的托管值误判为用户目录、不再接管，
    // 与 M87（改赋值目标）是不同失效面，故单列一条。
    id: 'M89-umu-managed-value-exclusion',
    file: 'src/YetAnotherGameLauncher.Core/Services/Umu/NativeUmuLauncher.cs',
    find: '&& !string.Equals(protonLogDir, AppPaths.LogDirectory, StringComparison.Ordinal);',
    replace: '&& true;',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.Umu.NativeUmuLauncherLaunchTests.BuildPlan_ProtonLogDirectoryUsesLauncherLogDirectory',
  },
  {
    // 2026-10-23 补档快速路径：版本已最新、仅档位不一致才允许跳过已有文件的整份 MD5。
    // 检测恒假后补档测试会请求已有 common（全量 MDS 模式），守卫应变红。
    id: 'M90-pack-add-fast-detection',
    file: 'src/YetAnotherGameLauncher.Core/Services/GameUpdateService.cs',
    find: '&& !string.Equals(localState.ResourceTier, requestedTier, StringComparison.Ordinal);',
    replace: '&& false;',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.ResourceTierUpdateTests.UpdateAsync_PackAdd_SkipsFullHashForPreexistingFiles',
  },
  {
    // 2026-10-23 补档快速路径：快速模式不得退化成全量校验修复——退化后同尺寸损坏的已有文件
    // 会被检出并重下（Requests 多出已有文件），快速路径测试应变红。
    // 注：Subset(manifest, downloaded) 本身是性能措施（只对下载文件算 MD5），行为上被
    // broken.Join(downloaded) 兜住，无可观察差异，故不作为变异点（2026-10-23 实测该变异存活）。
    id: 'M91-pack-add-fast-mode-not-full-verify',
    file: 'src/YetAnotherGameLauncher.Core/Services/GameInstallService.cs',
    find: 'if (verifyAllExistingFiles)',
    replace: 'if (DateTime.MinValue != DateTime.MaxValue || verifyAllExistingFiles)',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.GameInstallServiceTests.SyncAsync_FastMode_SkipsFullHashForPreexistingFiles',
  },
  {
    // 2026-10-23 档位版本一致性：去掉包版本比较后，base 3.7.1 + pack 3.7.0 的
    // 错版清单会被放行；全量清单守卫测试应变红。
    id: 'M92-kuro-tier-version-guard',
    file: 'src/YetAnotherGameLauncher.Channels.Kuro/KuroChannelApi.cs',
    find: 'if (!VersionsMatch(pack.Version, baseVersion))',
    replace: 'if (false)',
    project: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/YetAnotherGameLauncher.Channels.Kuro.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Channels.Kuro.Tests.dll',
    method: 'YetAnotherGameLauncher.Channels.Kuro.Tests.KuroResourcePackTests.GetManifest_TierPackVersionMismatch_ThrowsBeforeFetchingPackFiles',
  },
  {
    // 2026-10-23 预下载应用：修复轮必须按暂存档位取清单。改回用户当前选择后，
    // 暂存 HD、应用期切 UHD 的守卫测试会请求 UHD 清单并落 UHD 状态。
    id: 'M93-predownload-staged-tier-pin',
    file: 'src/YetAnotherGameLauncher.Core/Services/GameUpdateService.cs',
    find: 'var tier = staged.ResourceTier ?? RequestedTier(game, server, channel);',
    replace: 'var tier = RequestedTier(game, server, channel);',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.ResourceTierUpdateTests.ApplyPredownload_RepairRuns_UsesStagedTierNotNewSelection',
  },
  {
    // 2026-10-23 清单边界：去掉全量清单校验后，保留路径/重复/冲突/坏 MD5 都会被放行；
    // 保留路径守卫测试应变红。
    id: 'M94-kuro-full-manifest-boundary',
    file: 'src/YetAnotherGameLauncher.Channels.Kuro/KuroChannelApi.cs',
    find: 'ManifestBoundary.ValidateManifest(hdManifest, "Kuro full manifest");',
    replace: '// MUTATION: full manifest boundary validation removed',
    project: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/YetAnotherGameLauncher.Channels.Kuro.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Channels.Kuro.Tests.dll',
    method: 'YetAnotherGameLauncher.Channels.Kuro.Tests.KuroResourcePackTests.GetManifest_EntryUnderReservedPath_Throws',
  },
  {
    // 2026-10-23 清单边界消费端：废弃清单点名 .yagl/state.json 时必须跳过，
    // 去掉守卫后状态文件被删，守卫测试应变红。
    id: 'M95-deletefiles-reserved-guard',
    file: 'src/YetAnotherGameLauncher.Core/Services/IncrementalUpdateService.cs',
    find: 'if (ManifestBoundary.IsReservedPath(relative))',
    replace: 'if (false && ManifestBoundary.IsReservedPath(relative))',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.IncrementalUpdateServiceTests.ApplyAsync_DeleteFilesUnderReservedPaths_Spared',
  },
  {
    // 2026-10-23 清单边界：非 HD 基础清单必须裁掉目标档目录（再追加目标包）。
    // 退回"只裁其它档"后，base 里的目标档旧条目会与包条目并存，守卫测试应变红。
    id: 'M96-kuro-crop-selected-tier',
    file: 'src/YetAnotherGameLauncher.Channels.Kuro/KuroChannelApi.cs',
    find: 'baseEntries.Where(e => !ResourceTiers.All.Any(t => IsInTierDirectory(e.Dest, t))),',
    replace: 'baseEntries.Where(e => !ResourceTiers.OtherTiers(tier).Any(t => IsInTierDirectory(e.Dest, t))),',
    project: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/YetAnotherGameLauncher.Channels.Kuro.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Channels.Kuro.Tests.dll',
    method: 'YetAnotherGameLauncher.Channels.Kuro.Tests.KuroResourcePackTests.GetManifest_TargetTierBaseEntryCoveredByPack_UsesPackEntry',
  },
  {
    // 2026-10-23 补档快速路径：本次下载的文件仍必须过 MD5 后验（只信下载返回值会被写坏内容穿透）。
    // 跳过后验后同尺寸篡改内容会被当成功落盘，守卫测试应变红（测试数据同尺寸，尺寸差异兜不住）。
    id: 'M97-fast-mode-downloaded-md5-verify',
    file: 'src/YetAnotherGameLauncher.Core/Services/GameInstallService.cs',
    find: 'installDir, manifest, downloaded, state, progress, cancellationToken).ConfigureAwait(false);',
    replace: 'installDir, manifest, [], state, progress, cancellationToken).ConfigureAwait(false);',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.GameInstallServiceTests.SyncAsync_FastMode_StillVerifiesDownloadedFiles',
  },
  {
    // 2026-10-23 补档快速路径罕见回退：下载窗口内已有文件被删/改尺寸时必须当第二轮下载补下并
    // MD5 后验（不动其余已有文件）。跳过回退后被改动的文件残留（缺文件/尺寸不符），守卫测试应变红。
    id: 'M98-fast-mode-changed-file-fallback',
    file: 'src/YetAnotherGameLauncher.Core/Services/GameInstallService.cs',
    find: 'var fastPost = ManifestVerifier.VerifyFast(installDir, manifest);\n            if (!fastPost.IsComplete)',
    replace: 'var fastPost = ManifestVerifier.VerifyFast(installDir, manifest);\n            if (DateTime.MinValue == DateTime.MaxValue)',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.GameInstallServiceTests.SyncAsync_FastMode_ExistingFileChangedDuringDownload_RepairsItInSecondRound',
  },
  {
    // 2026-10-23 复审阻塞项 1 回归：增量入口若恢复按 toVersion 硬校验档位包版本，预下载窗口期
    // （toVersion ≠ 线上版、resourcePacks 单值）sd/uhd 的预下载必被挡死 → 预下载窗口用例应变红。
    // 2026-11-13 8be178b 把 legacy 增量分支拆到 GetLegacyIncrementalManifestAsync：插入点改为
    // 该方法的补丁索引拉取前（原 GetIncrementalManifestAsync 的 tier 计算已不在同一方法内）。
    id: 'M99-incremental-no-pack-version-gate',
    file: 'src/YetAnotherGameLauncher.Channels.Kuro/KuroChannelApi.cs',
    find: '            var patchIndexJson = await FetchTextAsync(',
    replace: '            if (tier != ResourceTiers.Hd && DateTime.MinValue != DateTime.MaxValue)\n            {\n                var packIndex0 = await FetchResourcePackIndexAsync(server, cancellationToken).ConfigureAwait(false);\n                EnsurePackVersion(RequireResourcePack(packIndex0, tier), tier, toVersion);\n            }\n\n            var patchIndexJson = await FetchTextAsync(',
    project: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/YetAnotherGameLauncher.Channels.Kuro.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Channels.Kuro.Tests.dll',
    method: 'YetAnotherGameLauncher.Channels.Kuro.Tests.KuroResourcePackTests.GetIncrementalManifest_PredownloadWindow_TierPackNotYetAtTarget_DoesNotHardFail',
  },
  {
    // 2026-10-23 复审项 2 回归：裁掉目标档条目后若不校验"被裁条目 ⊆ 档位包"，静默丢条目会被
    // 收尾清理当游离文件删除且无自愈路径 → 拒收守卫用例应变红。
    id: 'M100-kuro-target-tier-drop-guard',
    file: 'src/YetAnotherGameLauncher.Channels.Kuro/KuroChannelApi.cs',
    find: '        if (droppedTargetTier.Count > 0)',
    replace: '        if (DateTime.MinValue == DateTime.MaxValue && droppedTargetTier.Count > 0)',
    project: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/YetAnotherGameLauncher.Channels.Kuro.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Channels.Kuro.Tests.dll',
    method: 'YetAnotherGameLauncher.Channels.Kuro.Tests.KuroResourcePackTests.GetManifest_TargetTierBaseEntryMissingFromPack_RejectsInsteadOfDroppingIt',
  },
  {
    // 2026-10-23 复审项 3 回归：罕见回退若对整份清单判失败，一个与本次下载无关的同尺寸损坏
    // 文件会把补档升级成硬失败 → 回退容忍用例应变红。
    id: 'M101-fast-mode-fallback-scope',
    file: 'src/YetAnotherGameLauncher.Core/Services/GameInstallService.cs',
    find: 'installDir, manifest, late, state, progress, cancellationToken).ConfigureAwait(false);',
    replace: 'installDir, manifest, late, state, progress, cancellationToken).ConfigureAwait(false);\n                if (!ManifestVerifier.VerifyFull(installDir, manifest).IsComplete)\n                {\n                    throw new UpdateException("Post-download verification failed.");\n                }',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.GameInstallServiceTests.SyncAsync_FastMode_ConcurrentDamage_DoesNotFailOnUnrelatedCorruptExistingFile',
  },
  {
    // 2026-10-23 复审项 4 回归：Windows 吃掉段尾点/尾空格（`Saved.` → `Saved`、`pak.` → `pak`），
    // 去掉该拒收后 `Client/Content/HD/pak.` 会被放行 → 尾点/尾空格用例应变红。
    id: 'M102-manifest-trailing-dot-reject',
    file: 'src/YetAnotherGameLauncher.Core/Utilities/ManifestBoundary.cs',
    find: '            if (segment[^1] is \'.\' or \' \')',
    replace: '            if (DateTime.MinValue == DateTime.MaxValue && (segment[^1] is \'.\' or \' \'))',
    project: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/YetAnotherGameLauncher.Channels.Kuro.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Channels.Kuro.Tests.dll',
    method: 'YetAnotherGameLauncher.Channels.Kuro.Tests.KuroResourcePackTests.GetManifest_TrailingDotOrSpaceSegment_Throws',
  },
  {
    // 2026-10-23 复审项 4 回归（消费端纵深）：IsReservedPath 的尾点归一被去掉后 `Saved./slot.sav`
    // 逃过保留判定、被废弃清单删除 → 保留路径守卫用例应变红。
    id: 'M103-reserved-path-trailing-normalize',
    file: 'src/YetAnotherGameLauncher.Core/Utilities/ManifestBoundary.cs',
    find: '            var segment = trimmed.TrimEnd(\'.\', \' \');',
    replace: '            var segment = trimmed;',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.IncrementalUpdateServiceTests.ApplyAsync_DeleteFilesUnderReservedPaths_Spared',
  },
  {
    // 2026-10-26 复审 P2 回归：`"deleteFiles": [null]` 会被 STJ 解析出 null 元素，渠道侧跳过失守后
    // IsReservedPath 直接 NRE 掉整份清单获取 → 空白条目用例应变红。
    id: 'M104-blank-deletefiles-skip-channel',
    file: 'src/YetAnotherGameLauncher.Channels.Kuro/KuroChannelApi.cs',
    find: '                if (string.IsNullOrWhiteSpace(path))\n                {\n                    logger?.LogWarning("Kuro incremental deleteFiles has a blank entry; skipping it.");\n                    continue;\n                }',
    replace: '                if (DateTime.MinValue == DateTime.MaxValue)\n                {\n                    logger?.LogWarning("Kuro incremental deleteFiles has a blank entry; skipping it.");\n                    continue;\n                }',
    project: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/YetAnotherGameLauncher.Channels.Kuro.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Channels.Kuro.Tests.dll',
    method: 'YetAnotherGameLauncher.Channels.Kuro.Tests.KuroResourcePackTests.GetIncrementalManifest_BlankDeleteFilesEntry_SkippedInsteadOfThrowing',
  },
  {
    // 2026-10-26 复审 P2 回归（消费端）：旧暂存清单的 null deleteFiles 条目同理，跳过失守即 NRE 掉整轮更新
    id: 'M105-blank-deletefiles-skip-consumer',
    file: 'src/YetAnotherGameLauncher.Core/Services/IncrementalUpdateService.cs',
    find: '            if (string.IsNullOrWhiteSpace(relative))\n            {\n                logger?.LogWarning("deleteFiles has a blank entry; skipping it.");\n                continue;\n            }',
    replace: '            if (DateTime.MinValue == DateTime.MaxValue)\n            {\n                logger?.LogWarning("deleteFiles has a blank entry; skipping it.");\n                continue;\n            }',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.IncrementalUpdateServiceTests.ApplyAsync_BlankDeleteFilesEntry_SkippedInsteadOfThrowing',
  },
  {
    // 2026-10-26 复审 P3 回归：父目录弹出失守后 `data/../Saved/x` 归一成 `data/Saved/x`，
    // 逃过禁写判定而 ResolveSafe 仍落进 Saved/ → 点段绕过用例应变红。
    id: 'M106-reserved-path-dotdot-pop',
    file: 'src/YetAnotherGameLauncher.Core/Utilities/ManifestBoundary.cs',
    find: '            if (trimmed == "..")',
    replace: '            if (DateTime.MinValue == DateTime.MaxValue && trimmed == "..")',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.IncrementalUpdateServiceTests.ApplyAsync_DeleteFilesReservedPathWithDotSegments_Spared',
  },
  {
    // 2026-10-26 复审 P3 回归：重复 dest 的去重失守后清单带两条同路径条目（并发写同一目标、
    // 后到者覆盖先到者）→ 先登记者胜用例应变红。
    id: 'M107-manifest-duplicate-dedupe',
    file: 'src/YetAnotherGameLauncher.Channels.Kuro/KuroChannelApi.cs',
    find: '            .Where(entry => seen.Add(entry.Dest.Replace(\'\\\\\', \'/\')))',
    replace: '            .Where(entry => seen.Count >= 0)',
    project: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/YetAnotherGameLauncher.Channels.Kuro.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Channels.Kuro.Tests.dll',
    method: 'YetAnotherGameLauncher.Channels.Kuro.Tests.KuroResourcePackTests.GetManifest_DuplicatePaths_DedupesFirstWins',
  },
  {
    // 2026-10-26 复审小项：空白 MD5 一旦按"未提供"放行，下游 IsNullOrEmpty 口径会把它当
    // "已提供但比不上"而永久失败 → 畸形校验值用例应变红。
    id: 'M108-whitespace-md5-rejected',
    file: 'src/YetAnotherGameLauncher.Core/Utilities/ManifestBoundary.cs',
    find: '        if (!string.IsNullOrEmpty(file.Md5) && !IsMd5(file.Md5))',
    replace: '        if (!string.IsNullOrWhiteSpace(file.Md5) && !IsMd5(file.Md5))',
    project: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/YetAnotherGameLauncher.Channels.Kuro.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Channels.Kuro.Tests.dll',
    method: 'YetAnotherGameLauncher.Channels.Kuro.Tests.KuroResourcePackTests.GetManifest_WhitespaceMd5_Throws',
  },
  {
    // 2026-10-26 复审 nit 回归：真名只吃尾点、不吃"尾点后尾空格"，`Saved ./x` 归一成 `Saved `
    // 逃过禁写判定（Win32 连吃点与空格会落到 Saved/）→ 归一口径用例应变红。
    id: 'M109-canonicalize-trailing-dot-space',
    file: 'src/YetAnotherGameLauncher.Core/Utilities/ManifestBoundary.cs',
    find: '            var segment = trimmed.TrimEnd(\'.\', \' \');',
    replace: '            var segment = trimmed.TrimEnd(\'.\');',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Utilities.ManifestBoundaryTests.IsReservedPath_TargetLandsInReservedArea_ReturnsTrue',
  },
  {
    // 2026-10-26 复审 nit 回归：保留前缀判定退回原始串后 `./Client/Content/HD/x`、
    // `Client/Content/../Client/Content/HD/x` 匹配不上前缀，保留档位内容被废弃清单删掉。
    id: 'M110-deletefiles-prefix-canonical',
    file: 'src/YetAnotherGameLauncher.Core/Services/IncrementalUpdateService.cs',
    find: '            if (ManifestVerifier.IsUnderPreservedPrefix(canonical, preservedPrefixes))',
    replace: '            if (ManifestVerifier.IsUnderPreservedPrefix(relative, preservedPrefixes))',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.IncrementalUpdateServiceTests.ApplyAsync_DeleteFilesPreservedPrefixWithDotSegments_Spared',
  },
  {
    // 2026-10-26 复审 nit 回归：产物豁免退回原始串后 `./data/file.dat` 与 `data/file.dat`
    // 匹配不上，刚产出且校验通过的文件被删（RF-C 同族）。
    id: 'M111-deletefiles-produced-canonical',
    file: 'src/YetAnotherGameLauncher.Core/Services/IncrementalUpdateService.cs',
    find: '            if (producedThisUpdate.Contains(canonical))',
    replace: '            if (producedThisUpdate.Contains(relative))',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.IncrementalUpdateServiceTests.ApplyAsync_DeleteFilesListingFreshlyProducedFileWithDotSegments_SparedIt',
  },
  {
    // 2026-10-26 复审 P1 回归：空全量清单守卫失守后 `{}`/空 resource 会被当合法清单返回，
    // 同步收尾随后删光已装内容 → 空清单拒收用例应变红。
    id: 'M112-empty-full-manifest-guard',
    file: 'src/YetAnotherGameLauncher.Channels.Kuro/KuroChannelApi.cs',
    find: '        if (fileCount == 0)',
    replace: '        if (DateTime.MinValue == DateTime.MaxValue)',
    project: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/YetAnotherGameLauncher.Channels.Kuro.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Channels.Kuro.Tests.dll',
    method: 'YetAnotherGameLauncher.Channels.Kuro.Tests.KuroResourcePackTests.GetManifest_EmptyOrNullBase_RejectsInsteadOfReturningEmptyManifest',
  },
  {
    // 2026-10-26 复审 P1 回归（消费端兜底）：清理空清单守卫失守后，绕过渠道的注入清单会把
    // 安装目录删光 → 空清单不清理用例应变红。
    id: 'M113-empty-manifest-cleanup-guard',
    file: 'src/YetAnotherGameLauncher.Core/Services/GameInstallService.cs',
    find: '        if (manifest.Files.Count == 0)',
    replace: '        if (DateTime.MinValue == DateTime.MaxValue)',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.GameInstallServiceTests.SyncAsync_EmptyManifest_DoesNotDeleteInstalledFiles',
  },
  {
    // 2026-10-26 复审 P2 回归：删除清单退回严格 NormalizeRelativePath 后，目录条目/别名会被
    // 清单获取期拒收、增量更新被永久挡住 → 宽容形态用例应变红。
    id: 'M114-deletefiles-tolerance',
    file: 'src/YetAnotherGameLauncher.Core/Utilities/ManifestBoundary.cs',
    find: '            if (CanonicalizeWithinRoot(path) is null)\n            {\n                throw new UpdateException($"{source}: deleteFiles path escapes the install root: {path}");\n            }',
    replace: '            NormalizeRelativePath(path, source, "deleteFiles entry");',
    project: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/YetAnotherGameLauncher.Channels.Kuro.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Channels.Kuro.Tests.dll',
    method: 'YetAnotherGameLauncher.Channels.Kuro.Tests.KuroResourcePackTests.GetIncrementalManifest_DeleteFilesTolerantShapes_Kept',
  },
  {
    // 2026-10-26 复审 P2 回归：删除清单的逃逸拒收失守后 `../outside.pak` 会被放行 → 逃逸拒收用例应变红。
    id: 'M117-deletefiles-escape-reject',
    file: 'src/YetAnotherGameLauncher.Core/Utilities/ManifestBoundary.cs',
    find: '            if (CanonicalizeWithinRoot(path) is null)',
    replace: '            if (DateTime.MinValue == DateTime.MaxValue)',
    project: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/YetAnotherGameLauncher.Channels.Kuro.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Channels.Kuro.Tests.dll',
    method: 'YetAnotherGameLauncher.Channels.Kuro.Tests.KuroResourcePackTests.GetIncrementalManifest_DeleteFilesEscapingRoot_Rejected',
  },
  {
    // 2026-10-26 复审 P3 回归：档位目录判定不归一 '\' 时，base 的 `Client\Content\SD\x` 漏裁、
    // 与包条目叠成"双条目写同一文件" → 反斜杠裁剪用例应变红。
    id: 'M115-tier-dir-backslash-normalize',
    file: 'src/YetAnotherGameLauncher.Channels.Kuro/KuroChannelApi.cs',
    find: '        && dest.Replace(\'\\\\\', \'/\').StartsWith(PackDirectoryFor(tier) + "/", StringComparison.OrdinalIgnoreCase);',
    replace: '        && dest.StartsWith(PackDirectoryFor(tier) + "/", StringComparison.OrdinalIgnoreCase);',
    project: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/YetAnotherGameLauncher.Channels.Kuro.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Channels.Kuro.Tests.dll',
    method: 'YetAnotherGameLauncher.Channels.Kuro.Tests.KuroResourcePackTests.GetManifest_BackslashTierEntry_CroppedLikeSlashForm',
  },
  {
    // 2026-10-26 复审 P3 回归：`srcFiles: null` 不做 coalesce 时 ToManifestFiles 抛未分类异常
    // （ArgumentNullException）而非 UpdateException → null 集合用例应变红。
    id: 'M116-null-group-files-coalesce',
    file: 'src/YetAnotherGameLauncher.Channels.Kuro/KuroChannelApi.cs',
    find: 'ToManifestFiles(group.SrcFiles ?? [], cdn, resourcesBasePath, withUrl: false),',
    replace: 'ToManifestFiles(group.SrcFiles!, cdn, resourcesBasePath, withUrl: false),',
    project: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/YetAnotherGameLauncher.Channels.Kuro.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Channels.Kuro.Tests.dll',
    method: 'YetAnotherGameLauncher.Channels.Kuro.Tests.KuroResourcePackTests.GetIncrementalManifest_NullCollectionProperties_Coalesced',
  },
  {
    // 2026-10-26 复审回归：清理判定退回原始串后，清单里的 `data/./file.dat` 与磁盘上的
    // `data/file.dat` 匹配不上，清单内文件被当游离文件删掉 → 归一串用例应变红。
    id: 'M118-cleanup-manifest-normalize',
    file: 'src/YetAnotherGameLauncher.Core/Services/GameInstallService.cs',
    find: '            .Select(f => NormalizeForCleanup(f.Path))',
    replace: '            .Select(f => f.Path.Replace(\'\\\\\', \'/\'))',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.GameInstallServiceTests.SyncAsync_ManifestPathWithDotSegment_DoesNotDeleteListedFile',
  },
  {
    // 2026-10-26 复审扫尾：`resource` 条目的 dest 为 null 时 IsDiffResource 若不判空会 NRE
    //（不是 UpdateException，增量链无回退）→ 顶层 null dest 用例应变红。
    id: 'M119-incremental-null-dest-diff-check',
    file: 'src/YetAnotherGameLauncher.Channels.Kuro/KuroChannelApi.cs',
    find: '        !string.IsNullOrWhiteSpace(dest)\n        && (dest.EndsWith(".krpdiff", StringComparison.OrdinalIgnoreCase)\n            || dest.EndsWith(".krdiff", StringComparison.OrdinalIgnoreCase));',
    replace: '        (dest!.EndsWith(".krpdiff", StringComparison.OrdinalIgnoreCase)\n            || dest!.EndsWith(".krdiff", StringComparison.OrdinalIgnoreCase));',
    project: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/YetAnotherGameLauncher.Channels.Kuro.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Channels.Kuro.Tests.dll',
    method: 'YetAnotherGameLauncher.Channels.Kuro.Tests.KuroChannelApiTests.GetIncrementalManifest_NullDestInTopLevelResource_SkippedInsteadOfThrowing',
  },
  {
    // 2026-10-27 W2：GetState 的 SHA-256 判据——同长度内容篡改后，去掉哈希比较必须让
    // "Damaged"断言变红（尺寸分支不会替它兜底）。
    id: 'M122-native-state-hash-check',
    file: 'src/YetAnotherGameLauncher.Core/Dependencies/DependencyInstaller.cs',
    find: '                    || !string.Equals(Hashing.Sha256Hex(path), dll.Sha256, StringComparison.Ordinal))',
    replace: '                    || false)',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Dependencies.DependencyInstallerWinetricksTests.GetState_Installed_RequiresFilesHashesOverridesAndBackup',
  },
  {
    // 2026-10-27 W2：备份符号链接只记录 target，绝不跟随复制——去掉分支后备份形态变 File，守卫红。
    id: 'M123-native-backup-symlink-not-followed',
    file: 'src/YetAnotherGameLauncher.Core/Dependencies/DependencyInstaller.cs',
    find: '                var linkTarget = GetLinkTarget(path);',
    replace: '                var linkTarget = (string?)null;',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Dependencies.DependencyInstallerWinetricksTests.Install_HappyPath_SymlinkBackup_WinetricksArgvEnv_FilesOverrideMarker',
  },
  {
    // 2026-10-27 W2：Proton 升级语义——卸载必须按当前树重建链接，改回记录的旧 target 后守卫红。
    id: 'M124-native-uninstall-current-proton',
    file: 'src/YetAnotherGameLauncher.Core/Dependencies/DependencyInstaller.cs',
    find: '                        ? CurrentProtonDllPath(currentProtonRoot, dll)',
    replace: '                        ? entry.LinkTarget!',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Dependencies.DependencyInstallerWinetricksTests.Uninstall_Proton_RestoresCurrentTreeLinksAndRemovesOverrideAndState',
  },
  {
    // 2026-10-27 W2：argv 形态必须逐项进入 ProcessStartInfo.ArgumentList（含空格路径不拆分）。
    id: 'M125-process-argument-list',
    file: 'src/YetAnotherGameLauncher.Core/Services/SystemProcessRunner.cs',
    find: '        if (spec.ArgumentList is { Count: > 0 } arguments)',
    replace: '        if (spec.ArgumentList is { Count: > 100 } arguments)',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.SystemProcessRunnerTests.CreateStartInfo_ArgumentList_PreservesArgumentsVerbatim',
  },
  {
    // 2026-10-27 W2：File 备份的 SHA-256 记录必须合法——去掉校验后缺 SHA 的清单会被当有效备份。
    id: 'M127-native-backup-sha-required',
    file: 'src/YetAnotherGameLauncher.Core/Dependencies/DependencyInstaller.cs',
    find: '                        || !IsSha256Hex(entry.Sha256)',
    replace: '                        || false',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Dependencies.DependencyInstallerWinetricksTests.GetState_TamperedBackupManifest_BackupMissing_AndUninstallNoModification',
  },
  {
    // 2026-10-27 W2：清单 null 元素归一——去掉守卫后加载阶段 NRE（测试必须以失败收场）。
    id: 'M128-native-backup-null-entry',
    file: 'src/YetAnotherGameLauncher.Core/Dependencies/DependencyInstaller.cs',
    find: '                || backup.Dlls.Any(d => d is null)',
    replace: '                || false',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Dependencies.DependencyInstallerWinetricksTests.GetState_TamperedBackupManifest_BackupMissing_AndUninstallNoModification',
  },
  {
    // 2026-10-27 W2：字体来源携带空 dlls 也算越界字段——退回"仅非空才报错"后守卫红。
    id: 'M129-catalog-empty-dlls-rejected',
    file: 'src/YetAnotherGameLauncher.Core/Dependencies/DependencyCatalog.cs',
    find: '        if (manifest.Dlls is not null)',
    replace: '        if (manifest.Dlls is { Count: > 0 })',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Dependencies.DependencyCatalogTests.Parse_DownloadManifestWithEmptyDllFields_Rejected',
  },
  {
    // 2026-10-27 W2：字体来源携带空 dllOverrides 也算越界字段。
    id: 'M130-catalog-empty-overrides-rejected',
    file: 'src/YetAnotherGameLauncher.Core/Dependencies/DependencyCatalog.cs',
    find: '        if (manifest.DllOverrides is not null)',
    replace: '        if (manifest.DllOverrides is { Count: > 0 })',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Dependencies.DependencyCatalogTests.Parse_DownloadManifestWithEmptyDllFields_Rejected',
  },
  {
    // 2026-11-05 W2 复审：覆盖值查询超时不能降级成 Mismatched（否则 UI 把健康安装报 Damaged）。
    id: 'M131-native-state-timeout-not-damaged',
    file: 'src/YetAnotherGameLauncher.Core/Dependencies/DependencyInstaller.cs',
    find: '            return OverrideCheck.Unavailable;',
    replace: '            return OverrideCheck.Mismatched;',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Dependencies.DependencyInstallerWinetricksTests.GetState_OverrideQueryTimesOut_InstalledWithVerificationUnavailable',
  },
  {
    // 2026-11-05 W2 复审：没有 `.incomplete` 时，偏好“预提交”会把安装后丢失 marker/manifest
    // 的 prefix 误判为未安装 → 重新备份原生 DLL（data loss）。
    id: 'M132-native-postinstall-loss-not-precommit',
    file: 'src/YetAnotherGameLauncher.Core/Dependencies/DependencyInstaller.cs',
    find: '            || !File.Exists(pendingPath)',
    replace: '            || false',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Dependencies.DependencyInstallerWinetricksTests.GetState_NoMarkerNoManifestNoPending_BackupMissing',
  },
  {
    // 2026-11-05 W2 复审：非零 reg query 且父键探针失败时不能按 Mismatched（Damaged）处理。
    id: 'M133-native-override-probe-unavailable',
    file: 'src/YetAnotherGameLauncher.Core/Dependencies/DependencyInstaller.cs',
    find: 'return RunRegQuery(target, WineRegistryKey).Succeeded',
    replace: 'return true',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Dependencies.DependencyInstallerWinetricksTests.GetState_OverrideQueryNonZeroAndParentProbeFails_InstalledWithVerificationUnavailable',
  },
  {
    // 2026-11-05 W2 复审：安装路径不能无条件清理（必须复用 IsUncommittedBackupResidue）。
    id: 'M134-native-precommit-residue-reinstall',
    file: 'src/YetAnotherGameLauncher.Core/Dependencies/DependencyInstaller.cs',
    find: '                if (IsUncommittedBackupResidue(\n                        target, manifest, markerExists, manifestPath, pendingPath))',
    replace: '                if (true)',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Dependencies.DependencyInstallerWinetricksTests.Install_NoSentinelNoManifest_BackupMissingAndBackupKept',
  },
  {
    // 2026-11-05 W2 复审：DLL 已还原但覆盖仍是 App 值时不能当预提交（会把 App override 当原始值）。
    id: 'M135-native-partial-restore-overrides',
    file: 'src/YetAnotherGameLauncher.Core/Dependencies/DependencyInstaller.cs',
    find: '               || OverridesAllDifferFromExpected(target, manifest);',
    replace: '               || true;',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Dependencies.DependencyInstallerWinetricksTests.PendingSentinelWithInstalledOverrides_BackupMissingAndNoRebackup',
  },
  {
    // 2026-11-05 W2 复审：原生 DLL 未还原但覆盖被改回旧值（局部还原）也不能当预提交。
    id: 'M136-native-partial-restore-dlls',
    file: 'src/YetAnotherGameLauncher.Core/Dependencies/DependencyInstaller.cs',
    find: '            || DllsMatch(target, manifest))',
    replace: '            || false)',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Dependencies.DependencyInstallerWinetricksTests.PendingSentinelWithNativeDllsButOldOverrides_BackupMissing',
  },
  {
    // 2026-11-05 W2 复审：任一期望覆盖键仍匹配 App 值时必须判为局部还原，不能当预提交。
    id: 'M137-native-override-still-app-value',
    file: 'src/YetAnotherGameLauncher.Core/Dependencies/DependencyInstaller.cs',
    find: '                if (values.TryGetValue(key, out var actual)\n                    && string.Equals(actual, expectedValue, StringComparison.Ordinal))\n                {\n                    return false;\n                }',
    replace: '                if (values.TryGetValue(key, out var actual)\n                    && string.Equals(actual, expectedValue, StringComparison.Ordinal))\n                {\n                    return true;\n                }',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Dependencies.DependencyInstallerWinetricksTests.PendingSentinelWithInstalledOverrides_BackupMissingAndNoRebackup',
  },
  {
    // 2026-11-05 W2 复审：任一期望覆盖键仍匹配 App 值就不能当预提交（不能只看第一个键）。
    id: 'M138-native-partial-restore-override-keys',
    file: 'src/YetAnotherGameLauncher.Core/Dependencies/DependencyInstaller.cs',
    find: '            foreach (var (key, expectedValue) in expected)',
    replace: '            foreach (var (key, expectedValue) in expected.Take(1))',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Dependencies.DependencyInstallerWinetricksTests.PendingSentinelWithPartiallyRestoredOverrides_BackupMissing',
  },
  {
    // 2026-11-06 W2 复审：注册表值名大小写不敏感；改成 Ordinal 会漏掉 *MSFTEDIT 旧值。
    id: 'M139-registry-value-name-case-insensitive',
    file: 'src/YetAnotherGameLauncher.Core/Dependencies/DependencyInstaller.cs',
    find: '        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);',
    replace: '        var values = new Dictionary<string, string>(StringComparer.Ordinal);',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Dependencies.DependencyInstallerWinetricksTests.UppercaseRegistryValue_BackedUpComparedAndRestoredCaseInsensitively',
  },
  {
    // 2026-10-27 W1：内嵌 cjk-fonts 的 57 条替换（含 KRSDK H7GBK 别名）序列化守卫——
    // 关掉 Replacements 分支后 Build_EmbeddedCjkFonts... 必须红（无节 / 行数不足）。
    id: 'M120-cjk-replacements-serialized',
    file: 'src/YetAnotherGameLauncher.Core/Dependencies/WineRegistryScriptBuilder.cs',
    find: 'if (replacementGroups.Count > 0)',
    replace: 'if (replacementGroups.Count > 100)',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Dependencies.WineRegistryScriptBuilderTests.Build_EmbeddedCjkFonts_EmitsAllReplacementMappingsIncludingSdkAliases',
  },
  {
    // 2026-10-27 W1：.reg 必须以 UTF-16LE BOM 落盘——改成 UTF-8 后 H7 中文名写读回环守卫必须红。
    id: 'M121-cjk-reg-utf16le-bom',
    file: 'src/YetAnotherGameLauncher.Core/Dependencies/WineRegistryScriptBuilder.cs',
    find: 'await File.WriteAllTextAsync(path, content, Encoding.Unicode, cancellationToken).ConfigureAwait(false);',
    replace: 'await File.WriteAllTextAsync(path, content, Encoding.UTF8, cancellationToken).ConfigureAwait(false);',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Dependencies.WineRegistryScriptBuilderTests.WriteAsync_EmbeddedCjkFonts_RoundTripsH7AliasesAsUtf16LeBom',
  },
  {
    // 2026-11-06 dawn.wine Anubis WAF 403：HttpFileDownloader 必须在客户端未配置 User-Agent 时补默认值。
    id: 'M140-downloader-default-user-agent',
    file: 'src/YetAnotherGameLauncher.Core/Services/HttpFileDownloader.cs',
    find: '        if (httpClient.DefaultRequestHeaders.UserAgent.Count == 0)\n        {\n            requestMessage.Headers.UserAgent.ParseAdd(DefaultUserAgent);\n        }',
    replace: '',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.HttpFileDownloaderTests.DownloadFileAsync_ServerRejectsEmptyUserAgentWith403_SendsDefaultUserAgentAndSucceeds',
  },
  {
    // 2026-11-09 游戏运行中禁用启动按钮：去掉 !IsGameRunning 门控后启动态禁用守卫必须红。
    id: 'M141-launch-button-disabled-while-game-running',
    file: 'src/YetAnotherGameLauncher/ViewModels/GameItemViewModel.cs',
    find: '    public bool CanClickLaunch => !IsBusy && !IsGameRunning;',
    replace: '    public bool CanClickLaunch => !IsBusy;',
    project: 'tests/YetAnotherGameLauncher.App.Tests/YetAnotherGameLauncher.App.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.App.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.App.Tests.dll',
    method: 'YetAnotherGameLauncher.UiTests.DetailActionDockHeadlessTests.LaunchedGame_DisablesLaunchButton_AndShowsLaunchedText',
  },
  {
    // 2026-11-12 3.8 更新前 P0：收尾清理必须整段保留 UE 的 Client/Saved（3.8 清单只点名
    // Resources 子目录，SaveGames/Logs/Config 在清单外）；去掉守卫后存档被当游离文件删除。
    id: 'M142-client-saved-cleanup-preserved',
    file: 'src/YetAnotherGameLauncher.Core/Services/GameInstallService.cs',
    find: '            if (ManifestBoundary.IsClientSavedPath(relative))',
    replace: '            if (false && ManifestBoundary.IsClientSavedPath(relative))',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.GameInstallServiceTests.SyncAsync_CleansStaleFilesButPreservesClientSaved',
  },
  {
    // 2026-11-12 3.8 边界：Client/Saved 非 Resources 路径必须仍禁写；去掉嵌套判定后
    // SaveGames 可被清单覆盖/删除。
    id: 'M143-client-saved-boundary-reserved',
    file: 'src/YetAnotherGameLauncher.Core/Utilities/ManifestBoundary.cs',
    find: '        if (IsClientSavedPath(canonical))',
    replace: '        if (false && IsClientSavedPath(canonical))',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Utilities.ManifestBoundaryTests.ValidateManifest_ClientSavedOtherPaths_RemainReservedEvenWithAllowlist',
  },
  {
    // 2026-11-12 3.8 旧索引守卫：bundle 不可用时不能把旧 3.7.1 内容标成请求的 3.8.0；
    // 去掉版本守卫后 GetManifestAsync 会返回错误标签的旧清单。
    id: 'M144-kuro-legacy-mislabel-guard',
    file: 'src/YetAnotherGameLauncher.Channels.Kuro/KuroChannelApi.cs',
    find: '        if (VersionComparison.IsNewer(version, legacyVersion))',
    replace: '        if (false)',
    project: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/YetAnotherGameLauncher.Channels.Kuro.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Channels.Kuro.Tests.dll',
    method: 'YetAnotherGameLauncher.Channels.Kuro.Tests.KuroBundleIndexTests.GetManifest_NewerVersionWithoutBundle_FallsBackToLegacyAndRejectsMislabelling',
  },
  {
    // 2026-11-12 3.8 全量逐包闸：单包为空/残缺时合并后总数仍可能非空；去掉逐包非空守卫后
    // 空档位包会与 common 合并，收尾清理把该档已装内容当游离文件删除。
    id: 'M145-kuro-bundle-pack-nonempty',
    file: 'src/YetAnotherGameLauncher.Channels.Kuro/KuroChannelApi.cs',
    find: '        if (files.Count == 0)',
    replace: '        if (false)',
    project: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/YetAnotherGameLauncher.Channels.Kuro.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Channels.Kuro.Tests.dll',
    method: 'YetAnotherGameLauncher.Channels.Kuro.Tests.KuroBundleIndexTests.GetManifest_BundleIndex_TierPackEmpty_Throws',
  },
  {
    // 2026-11-12 预下载提示一致性：可见性判断必须与实际增量清单同构（common + 各档位包的补丁都可用）。
    // 把档位补丁判定取反后，缺档位补丁的数据仍会显示预下载提示，而点击必失败。
    id: 'M146-kuro-predownload-tier-patch-visible',
    file: 'src/YetAnotherGameLauncher.Channels.Kuro/KuroChannelApi.cs',
    find: '                || !HasUsablePatch(GetPack(packs, tier)?.PatchConfig))',
    replace: '                || HasUsablePatch(GetPack(packs, tier)?.PatchConfig))',
    project: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/YetAnotherGameLauncher.Channels.Kuro.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Channels.Kuro.Tests.dll',
    method: 'YetAnotherGameLauncher.Channels.Kuro.Tests.KuroBundleIndexTests.GetVersionInfo_BundleIndex_PredownloadWithoutTierPatch_HidesWindow',
  },
  {
    // 2026-11-12 预下载提示与计划层同口径：补丁入口必须版本非空（计划层过滤空 Version），
    // 否则畸形条目点亮预下载、计划落到不支持的 FullSync。去掉版本判定后守卫必须红。
    id: 'M147-kuro-predownload-patch-version',
    file: 'src/YetAnotherGameLauncher.Channels.Kuro/KuroChannelApi.cs',
    find: '        && patchConfig.Any(p => !string.IsNullOrEmpty(p.Version)\n                                && !string.IsNullOrWhiteSpace(p.IndexFile));',
    replace: '        && patchConfig.Any(p => !string.IsNullOrWhiteSpace(p.IndexFile));',
    project: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/YetAnotherGameLauncher.Channels.Kuro.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Channels.Kuro.Tests.dll',
    method: 'YetAnotherGameLauncher.Channels.Kuro.Tests.KuroBundleIndexTests.GetVersionInfo_BundleIndex_PredownloadPatchWithoutVersion_HidesWindow',
  },
  {
    // 2026-11-13 3.8 增量事故：按组回收差分草稿是峰值守卫本身；去掉后组 1 的 olddir 会留到整轮结束，
    // 失败轮用例必须红（整轮末尾清理只在全部成功时执行，掩盖不了这条）。
    id: 'M148-incremental-per-group-cleanup',
    file: 'src/YetAnotherGameLauncher.Core/Services/IncrementalUpdateService.cs',
    find: '            FileUtilities.TryDeleteDirectory(GroupOldDir(workDir, i), logger);',
    replace: '            // mutated: per-group draft cleanup disabled',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.IncrementalUpdateServiceTests.ApplyAsync_SecondGroupFailure_CleansFirstGroupWorkDirAndPatch',
  },
  {
    // 2026-11-13 3.8 增量事故：每组空间预检是"写满磁盘才失败"的唯一防线；条件改成恒真后，
    // 空间不足用例必须红（不会再抛 UpdateException）。
    id: 'M149-incremental-group-free-space-guard',
    file: 'src/YetAnotherGameLauncher.Core/Services/IncrementalUpdateService.cs',
    find: '        if (free.Value >= required)',
    replace: '        if (free.Value >= -1)',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.IncrementalUpdateServiceTests.ApplyAsync_InsufficientFreeSpace_ThrowsBeforeApplyingGroup',
  },
  {
    // 2026-11-13 复审 R3：保守估算的核心是"计入 src 副本"；删掉 src 项后，免费额度落在
    // src 与 src+dst 之间的用例必须红（只有 dst 的估算在这些机器上会误放行）。
    id: 'M150-incremental-free-space-src-term',
    file: 'src/YetAnotherGameLauncher.Core/Services/IncrementalUpdateService.cs',
    find: 'group.SrcFiles.Sum(f => Math.Max(0, f.Size))',
    replace: '0L',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.IncrementalUpdateServiceTests.ApplyAsync_FreeSpaceRequired_IncludesSourceCopies',
  },
  {
    // 2026-11-13 复审：预检必须按"该组本次是否真的复制源"计费；退回"补丁在就算 src"后，源缺失
    // 且空闲落在 dst 与 src+dst 之间的恢复路径会误拦，用例必须红。
    id: 'M151-incremental-fallback-free-space-dst-only',
    file: 'src/YetAnotherGameLauncher.Core/Services/IncrementalUpdateService.cs',
    find: '            var copiesSources = patchStaged\n                                && group.SrcFiles.All(src => File.Exists(ManifestVerifier.ResolveSafe(installDir, src.Path)));',
    replace: '            var copiesSources = patchStaged;',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.IncrementalUpdateServiceTests.ApplyAsync_MissingSource_FreeSpaceCheckedAgainstProducedFilesOnly',
  },
  {
    // 2026-11-13 鸣潮 3.8 背景哈希迁移：bundle 索引的 config.functionCode 分支必须被读到——
    // 关掉后新解析器测试会退回旧 launcher-config（未铺桩）并返回 null，用例必须红
    id: 'M152-kuro-bundle-background-hash',
    file: 'src/YetAnotherGameLauncher.Channels.Kuro/KuroSwitchConfigClient.cs',
    find: '? ReadBackgroundHash(config)',
    replace: '? null',
    project: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/YetAnotherGameLauncher.Channels.Kuro.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Channels.Kuro.Tests.dll',
    method: 'YetAnotherGameLauncher.Channels.Kuro.Tests.KuroSwitchConfigClientTests.FetchAsync_GameConfigWithHash_UsesIndexRelativePathAndSkipsLegacy',
  },
  {
    // 2026-11-13 缓存格式迁移：放行过期 formatVersion 后，旧 meta 不再强制重解析，
    // 迁移守卫用例必须红（版本门控会把旧投放永久锁死）。
    id: 'M153-backdrop-format-gate',
    file: 'src/YetAnotherGameLauncher.Core/Services/GameBackdropService.cs',
    find: '&& cached.FormatVersion >= minFormatVersion',
    replace: '&& cached.FormatVersion >= 0',
    project: 'tests/YetAnotherGameLauncher.Core.Tests/YetAnotherGameLauncher.Core.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Core.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Core.Tests.dll',
    method: 'YetAnotherGameLauncher.Core.Tests.Services.GameBackdropServiceTests.Resolve_LegacyFormatCache_ForcesOneResolverCallThenUpgrades',
  },
  {
    // 2026-11-13 VM 触发点：GetCachedGameVersion 不再把格式过期的缓存报为可用时，
    // 启动刷新必须仍能触发一次远程解析（否则服务层改了也到不了解析器）。
    id: 'M154-backdrop-vm-format-trigger',
    file: 'src/YetAnotherGameLauncher.Core/Services/GameBackdropService.cs',
    find: '|| meta.FormatVersion < resolver.CacheFormatVersion',
    replace: '|| false',
    project: 'tests/YetAnotherGameLauncher.App.Tests/YetAnotherGameLauncher.App.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.App.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.App.Tests.dll',
    method: 'YetAnotherGameLauncher.AppTests.StartupAssetPreloadTests.LegacyBackdropCacheFormat_TriggersOneRemoteResolve_EvenWhenVersionUnchanged',
  },
  {
    // 2026-11-13 真实解析器迁移常量：Kuro 必须为 2，否则旧 meta 不会被强制重解析，
    // 用户报告的"背景停在旧投放"会原样复现。
    id: 'M155-kuro-cache-format-version',
    file: 'src/YetAnotherGameLauncher.Channels.Kuro/KuroBackdropResolver.cs',
    find: 'public int CacheFormatVersion => 2;',
    replace: 'public int CacheFormatVersion => 1;',
    project: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/YetAnotherGameLauncher.Channels.Kuro.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Channels.Kuro.Tests.dll',
    method: 'YetAnotherGameLauncher.Channels.Kuro.Tests.KuroBackdropResolverTests.CacheFormatVersion_IsBumpedForBundleIndexMigration',
  },
  {
    // 2026-11-13 review 修复：bundle 形态索引缺背景字段时必须直接返回 null，
    // 改回无条件回退后旧 launcher-config 会把 3.7.1 旧投放写成当前缓存。
    id: 'M156-kuro-bundle-index-no-fallback',
    file: 'src/YetAnotherGameLauncher.Channels.Kuro/KuroSwitchConfigClient.cs',
    find: 'return (null, !isBundleIndex);',
    replace: 'return (null, true);',
    project: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/YetAnotherGameLauncher.Channels.Kuro.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Channels.Kuro.Tests.dll',
    method: 'YetAnotherGameLauncher.Channels.Kuro.Tests.KuroSwitchConfigClientTests.FetchAsync_BundleIndexWithoutHash_DoesNotFallBackToLegacy',
  },
];

function run(cmd, args) {
  const r = spawnSync(cmd, args, { encoding: 'utf8', cwd: root });
  return { code: r.status, out: `${r.stdout ?? ''}${r.stderr ?? ''}` };
}

const only = process.argv.slice(2);
const batch = only.length > 0 ? MUTATIONS.filter((m) => only.includes(m.id)) : MUTATIONS;
const unknown = only.filter((id) => !MUTATIONS.some((m) => m.id === id));
if (unknown.length > 0) {
  console.error(`未知变异 id：${unknown.join(', ')}（可选：${MUTATIONS.map((m) => m.id).join(', ')}）`);
  process.exit(2);
}

// 脏树守卫（2026-09-19 review 实锤）：还原用 git checkout -- <file>，会把目标文件上的
// 未提交改动一并丢弃（本次实际吃掉一个未提交的修复）。目标文件必须先提交再跑冒烟。
const dirty = run('git', ['status', '--porcelain', '--', ...batch.map((m) => m.file)]);
if (dirty.out.trim().length > 0) {
  console.error('以下变异目标文件带有未提交改动，git checkout 还原会销毁它们；请先提交：');
  console.error(dirty.out.trimEnd());
  process.exit(2);
}

let failures = 0;
const touchedProjects = new Set();

for (const m of batch) {
  const abs = path.join(root, m.file);
  const original = readFileSync(abs, 'utf8');

  if (m.find === m.replace) {
    console.error(`[spec] ${m.id}: find 与 replace 相同（规格错误）`);
    failures++;
    continue;
  }

  const occurrences = original.split(m.find).length - 1;
  if (occurrences !== 1) {
    console.error(`[stale] ${m.id}: find 在 ${m.file} 中出现 ${occurrences} 次（应为 1）——源码已漂移，请更新变异规格`);
    failures++;
    continue;
  }

  try {
    writeFileSync(abs, original.replace(m.find, m.replace));
    touchedProjects.add(m.project);

    const build = run('dotnet', ['build', m.project, '--nologo', '-v', 'q']);
    if (build.code !== 0) {
      console.error(`[build-fail] ${m.id}: 变异后测试工程构建失败（规格需检查）\n${build.out.slice(-2000)}`);
      failures++;
      continue;
    }

    const test = run('dotnet', [m.dll, '-method', m.method]);
    if (test.code === 0) {
      console.error(`[SURVIVED] ${m.id}: ${m.method} 未变红——守卫测试空心化，禁止合入`);
      failures++;
    } else {
      console.log(`[killed] ${m.id} → ${m.method}`);
    }
  } finally {
    run('git', ['checkout', '--', m.file]); // 无论成败都还原源码
  }
}

// 收尾：还原后的源码重建（测试 bin 恢复未变异状态），并确认工作树干净
const leftover = run('git', ['status', '--porcelain', '--', ...batch.map((m) => m.file)]);
if (leftover.out.trim().length > 0) {
  console.error(`[dirty] 变异源码未完全还原：\n${leftover.out}`);
  failures++;
}

if (touchedProjects.size > 0) {
  console.log('还原后重建测试工程…');
  for (const project of touchedProjects) {
    const build = run('dotnet', ['build', project, '--nologo', '-v', 'q']);
    if (build.code !== 0) {
      console.error(`[restore-build-fail] ${project}\n${build.out.slice(-2000)}`);
      failures++;
    }
  }
}

console.log(failures === 0
  ? `变异冒烟批通过：${batch.length} 个变异全部被击杀。`
  : `变异冒烟批失败：${failures} 个问题（击杀率 ${batch.length - failures}/${batch.length}）。`);
process.exit(failures === 0 ? 0 : 1);
