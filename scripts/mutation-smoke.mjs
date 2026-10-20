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
    find: 'var directResources = patchIndexFile.Resource\n                .Where(r => !IsDiffResource(r.Dest) && !otherTiers.Any(t => IsInTierDirectory(r.Dest, t)))\n                .ToList();',
    replace: 'var directResources = patchIndexFile.Resource\n                .Where(r => !otherTiers.Any(t => IsInTierDirectory(r.Dest, t)))\n                .ToList();',
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
    find: 'if (producedThisUpdate.Contains(relative))',
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
    find: '                        IsEnabled="{Binding !IsBusy}"\n',
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
    find: 'indexFile.Resource.Where(e => !otherTiers.Any(t => IsInTierDirectory(e.Dest, t))),',
    replace: 'indexFile.Resource.Where(e => true),',
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
    find: '                    .. (patchIndexFile.DeleteFiles ?? [])\n                        .Where(d => !otherTiers.Any(t => IsInTierDirectory(d, t))),',
    replace: '                    .. (patchIndexFile.DeleteFiles ?? []),',
    project: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/YetAnotherGameLauncher.Channels.Kuro.Tests.csproj',
    dll: 'tests/YetAnotherGameLauncher.Channels.Kuro.Tests/bin/Debug/net10.0/YetAnotherGameLauncher.Channels.Kuro.Tests.dll',
    method: 'YetAnotherGameLauncher.Channels.Kuro.Tests.KuroResourcePackTests.GetIncrementalManifest_SdTier_CropsOtherTierFilesAndGroups',
  },
  {
    // 2026-10-19 review（阻塞项）消费端纵深：废弃清单点名保留档位目录内的内容必须豁免——
    // 渠道漏裁时这里兜底，删掉即"档位内容被隐式删除"且用户无确认机会。
    id: 'M61-preserved-prefix-delete-guard',
    file: 'src/YetAnotherGameLauncher.Core/Services/IncrementalUpdateService.cs',
    find: 'if (ManifestVerifier.IsUnderPreservedPrefix(relative, preservedPrefixes))',
    replace: 'if (false && ManifestVerifier.IsUnderPreservedPrefix(relative, preservedPrefixes))',
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
