using Microsoft.Extensions.Logging;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;

namespace YetAnotherGameLauncher.Core.Services;

/// <summary>一次更新动作的结果。</summary>
public sealed record UpdateOutcome(
    UpdateStrategy Strategy,
    string FromVersion,
    string ToVersion,
    int RepairedFiles);

/// <summary>预下载动作的结果摘要。</summary>
public sealed record PredownloadSummary(string FromVersion, string ToVersion, long TotalBytes);

/// <summary>
/// 面向 UI 的更新编排入口：版本检查 → 计划（全量/增量）→ 执行 → 事后校验修复 → 落盘本地版本。
/// 预下载与应用拆分为两个公开方法，支持"下载完成后由用户确认再应用"。
/// </summary>
public sealed class GameUpdateService(
    IDownloader downloader,
    IPatchApplier patchApplier,
    GameInstallServiceOptions? options = null,
    ILogger? logger = null)
{
    /// <summary>一步到位更新：检查版本并生成计划，按计划走增量或全量，成功后把新版本写入本地状态。</summary>
    public async Task<UpdateOutcome> UpdateAsync(
        string installDir,
        GameDefinition game,
        GameServer server,
        IGameChannelApi channel,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var state = new LocalStateService(installDir);
        var localVersion = GetLocalVersion(state, game, server);
        var info = await channel.GetVersionInfoAsync(server, cancellationToken).ConfigureAwait(false);

        // 空版本拒收：渠道层已把版本缺失当错误，这里防漏检的实现（含测试替身）穿透——
        // 空目标版本会走全量"更新到空"并把空串落盘，此后 IsNewer 恒判无更新，
        // 游戏永久失去更新检测且无自愈路径（2026-09-20 复审修复）
        if (string.IsNullOrEmpty(info.LatestVersion))
        {
            throw new UpdateException($"Refusing to update {game.DisplayName}: channel reports no version.");
        }

        var plan = UpdatePlanner.Plan(localVersion, info.LatestVersion, info.PatchSourceVersions);

        var repaired = plan.Strategy == UpdateStrategy.Incremental
            ? await UpdateIncrementalAsync(installDir, server, channel, plan, progress, cancellationToken).ConfigureAwait(false)
            : await UpdateFullAsync(installDir, server, channel, plan, progress, cancellationToken).ConfigureAwait(false);

        await state.SaveAsync(
            new LocalGameState { GameId = game.Id, ServerId = server.Id, Version = plan.ToVersion },
            cancellationToken).ConfigureAwait(false);

        logger?.LogInformation("{Game} update finished: {From} -> {To} ({Strategy}, repaired {Repaired} files)",
            game.DisplayName, plan.FromVersion, plan.ToVersion, plan.Strategy, repaired);
        return new UpdateOutcome(plan.Strategy, plan.FromVersion, plan.ToVersion, repaired);
    }

    /// <summary>预下载：仅下载差分内容到暂存目录，不改动游戏本体。</summary>
    public async Task<PredownloadSummary> PredownloadAsync(
        string installDir,
        GameDefinition game,
        GameServer server,
        IGameChannelApi channel,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var state = new LocalStateService(installDir);
        var localVersion = GetLocalVersion(state, game, server);
        var info = await channel.GetVersionInfoAsync(server, cancellationToken).ConfigureAwait(false);

        if (!info.PredownloadAvailable || string.IsNullOrEmpty(info.PredownloadVersion))
        {
            throw new UpdateException("No predownload is currently open.");
        }

        // 本地版本已达到/超过预下载目标（预下载已应用或正式更新已到位而服务器窗口未关）：
        // 短路为幂等空操作（F66）——否则包式渠道落 GetPredownloadManifestAsync 无条件整包重下
        // 数十 GB（Apply 后暂存已删、IsArchiveIntact 无缓存可命中），Kuro 侧落下方
        // "no patch for local version V2"的自相矛盾文案（此时 local 就是 V2）。
        // 未安装（localVersion null）不短路：全新安装前预下载是合法路径
        if (localVersion is not null && !VersionComparison.IsNewer(info.PredownloadVersion, localVersion))
        {
            logger?.LogInformation(
                "Predownload short-circuited: local version {Local} already covers target {Target}.",
                localVersion, info.PredownloadVersion);
            return new PredownloadSummary(localVersion, info.PredownloadVersion, 0);
        }

        var plan = UpdatePlanner.Plan(localVersion, info.PredownloadVersion, info.PredownloadPatchSourceVersions);
        if (plan.Strategy == UpdateStrategy.Incremental)
        {
            var manifest = await channel.GetIncrementalManifestAsync(
                               server, plan.FromVersion, plan.ToVersion, cancellationToken).ConfigureAwait(false)
                           ?? throw new UpdateException("Predownload incremental manifest is unavailable.");

            var incremental = new IncrementalUpdateService(downloader, patchApplier, logger);
            await incremental.PredownloadAsync(installDir, manifest, progress, cancellationToken).ConfigureAwait(false);

            var totalBytes = manifest.Groups.Sum(g => g.PatchSize) + manifest.Files.Sum(f => f.Size);
            return new PredownloadSummary(plan.FromVersion, plan.ToVersion, totalBytes);
        }

        // 包式渠道（整包预下载）
        var packageManifest = await channel.GetPredownloadManifestAsync(server, cancellationToken).ConfigureAwait(false)
                              ?? throw new UpdateException(
                                  $"Predownload version {info.PredownloadVersion} has no patch for local version {plan.FromVersion}; wait for the full update.");

        var packages = new PackageInstallerService(downloader, logger);
        await packages.PredownloadAsync(installDir, packageManifest, progress, cancellationToken).ConfigureAwait(false);
        return new PredownloadSummary(plan.FromVersion, plan.ToVersion, packageManifest.Files.Sum(f => f.Size));
    }

    /// <summary>应用已预下载的内容并收尾（事后校验修复 + 更新本地版本）。</summary>
    public async Task<UpdateOutcome> ApplyPredownloadAsync(
        string installDir,
        GameDefinition game,
        GameServer server,
        IGameChannelApi channel,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var staged = IncrementalUpdateService.TryLoadStagedManifest(installDir)
                     ?? throw new UpdateException("No preloaded update found; run predownload first.");

        int repaired;
        if (staged.EntriesAreArchives)
        {
            var packages = new PackageInstallerService(downloader, logger);
            await packages.ApplyPredownloadAsync(installDir, staged, progress, cancellationToken).ConfigureAwait(false);
            repaired = 0;
        }
        else
        {
            var incremental = new IncrementalUpdateService(downloader, patchApplier, logger);
            await incremental.ApplyAsync(
                installDir, staged, progress, cancellationToken,
                dstUrlResolver: CreateDstUrlResolver(server, channel, staged.Version)).ConfigureAwait(false);
            repaired = await RepairPredownloadAsync(
                installDir, server, channel, staged, progress, cancellationToken).ConfigureAwait(false);
        }

        await new LocalStateService(installDir).SaveAsync(
            new LocalGameState { GameId = game.Id, ServerId = server.Id, Version = staged.Version },
            cancellationToken).ConfigureAwait(false);

        return new UpdateOutcome(
            staged.EntriesAreArchives ? UpdateStrategy.FullSync : UpdateStrategy.Incremental,
            "", staged.Version, repaired);
    }

    /// <summary>组级回退的产物直链解析缝：懒取目标版本全量清单建 相对路径→URL 映射，仅在首个
    /// 差分源缺失触发回退时才联网取全量清单（happy path 零额外请求）。协议实证（2026-10-02 真机）：
    /// 官方增量 dstFiles 原始条目无 url 字段，但组 dst 路径被全量清单全覆盖，且
    /// {resourcesBasePath}/{dest} 直链实测可下、md5 与增量 dstFiles 逐字符一致。解析失败按 null
    /// 折算（回退链收尾为 "use the full update"），日志留痕；懒任务记住首个取消令牌（单次
    /// apply 内单一令牌，无跨令牌复用面）。</summary>
    private static Func<string, CancellationToken, Task<string?>> CreateDstUrlResolver(
        GameServer server, IGameChannelApi channel, string version, ILogger? logger = null)
    {
        Task<IReadOnlyDictionary<string, string>>? mapTask = null;
        return async (path, cancellationToken) =>
        {
            try
            {
                mapTask ??= LoadDstUrlMapAsync(server, channel, version, cancellationToken);
                var map = await mapTask.ConfigureAwait(false);
                return map.GetValueOrDefault(path);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(
                    ex, "Direct-download resolver could not load the full manifest for {Version}", version);
                return null;
            }
        };

        static async Task<IReadOnlyDictionary<string, string>> LoadDstUrlMapAsync(
            GameServer server, IGameChannelApi channel, string version, CancellationToken cancellationToken)
        {
            var manifest = await channel.GetManifestAsync(server, version, cancellationToken).ConfigureAwait(false);
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in manifest.Files)
            {
                if (file.Url is not null)
                {
                    map.TryAdd(file.Path, file.Url); // 先者胜（RF-1 同族：重复 dest 不抛）
                }
            }

            return map;
        }
    }

    private async Task<int> UpdateFullAsync(
        string installDir,
        GameServer server,
        IGameChannelApi channel,
        UpdatePlan plan,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        var manifest = await channel.GetManifestAsync(server, plan.ToVersion, cancellationToken).ConfigureAwait(false);

        if (manifest.EntriesAreArchives)
        {
            var packages = new PackageInstallerService(downloader, logger);
            await packages.InstallAsync(installDir, manifest, progress, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var installer = new GameInstallService(downloader, options, logger);
        return await installer.SyncAsync(installDir, manifest, progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> UpdateIncrementalAsync(
        string installDir,
        GameServer server,
        IGameChannelApi channel,
        UpdatePlan plan,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        var manifest = await channel.GetIncrementalManifestAsync(
                           server, plan.FromVersion, plan.ToVersion, cancellationToken).ConfigureAwait(false)
                       ?? throw new UpdateException("Incremental manifest unavailable; use the full update instead.");

        // 差分清单必须是文件清单：archive 形态（EntriesAreArchives）交给 IncrementalUpdateService
        // 会把压缩包原样搬进安装根（当前两渠道不可达——包式渠道无差分入口，纯防御缺口）
        if (manifest.EntriesAreArchives)
        {
            throw new UpdateException("Incremental manifest unexpectedly contains archives; refusing to stage them as game files.");
        }

        var incremental = new IncrementalUpdateService(downloader, patchApplier, logger);
        await incremental.PredownloadAsync(installDir, manifest, progress, cancellationToken).ConfigureAwait(false);
        await incremental.ApplyAsync(installDir, manifest, progress, cancellationToken).ConfigureAwait(false);

        return await RepairAgainstManifestAsync(
            installDir, server, channel, plan.ToVersion, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 预下载应用后的事后校验修复，带服务器版本守卫：清单接口只能取到"服务器当前最新"的内容，
    /// 暂存却是 <c>staged.Version</c>。窗口期内提前应用（服务器 latest 尚未切到 staged.Version）时，
    /// 按 latest 清单修复会把刚打到新版的文件改回旧内容，且落盘版本号新、内容旧，之后无自愈路径——
    /// 此时跳过修复只记日志；版本一致的常规场景照常修复（缺口由下次 UpdateAsync 收敛）。
    /// </summary>
    private async Task<int> RepairPredownloadAsync(
        string installDir,
        GameServer server,
        IGameChannelApi channel,
        GameManifest staged,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        var info = await channel.GetVersionInfoAsync(server, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(info.LatestVersion, staged.Version, StringComparison.Ordinal))
        {
            logger?.LogInformation(
                "Skip post-apply repair: server latest {Latest} does not match staged {Staged}.",
                info.LatestVersion, staged.Version);
            return 0;
        }

        return await RepairAgainstManifestAsync(
            installDir, server, channel, staged.Version, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按目标版本全量清单做事后校验，修复缺失/损坏文件（通常为 0 个）。</summary>
    private async Task<int> RepairAgainstManifestAsync(
        string installDir,
        GameServer server,
        IGameChannelApi channel,
        string version,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        var manifest = await channel.GetManifestAsync(server, version, cancellationToken).ConfigureAwait(false);
        var before = ManifestVerifier.VerifyFast(
            installDir, manifest,
            (checkedCount, total) => progress?.Report(
                new UpdateProgress(UpdatePhase.Checking, 0, 0, checkedCount, total, null)));
        if (before.IsComplete)
        {
            return 0;
        }

        var installer = new GameInstallService(downloader, options, logger);
        return await installer.SyncAsync(installDir, manifest, progress, cancellationToken).ConfigureAwait(false);
    }

    private static string? GetLocalVersion(LocalStateService state, GameDefinition game, GameServer server) =>
        state.Load(game.Id, server.Id)?.Version;
}
