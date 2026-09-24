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
    // 2026-09-24 review 扩容：tar 链接目标的 ".." 穿越判定失效必须红（F11 守卫）
    id: 'M17-tar-link-target-escape',
    file: 'src/YetAnotherGameLauncher/Services/UmuComponentProvisioner.cs',
    find: "|| normalizedLink.Split('/').Contains(\"..\")",
    replace: '|| false /* MUTATION */',
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
