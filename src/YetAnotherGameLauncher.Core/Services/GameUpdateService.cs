using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using Microsoft.Extensions.Logging;

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
        var info = await channel.GetVersionInfoAsync(server, cancellationToken);
        var plan = UpdatePlanner.Plan(localVersion, info.LatestVersion, info.PatchSourceVersions);

        var repaired = plan.Strategy == UpdateStrategy.Incremental
            ? await UpdateIncrementalAsync(installDir, server, channel, plan, progress, cancellationToken)
            : await UpdateFullAsync(installDir, server, channel, plan, progress, cancellationToken);

        await state.SaveAsync(
            new LocalGameState { GameId = game.Id, ServerId = server.Id, Version = plan.ToVersion },
            cancellationToken);

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
        var info = await channel.GetVersionInfoAsync(server, cancellationToken);

        if (!info.PredownloadAvailable || string.IsNullOrEmpty(info.PredownloadVersion))
        {
            throw new UpdateException("No predownload is currently open.");
        }

        var plan = UpdatePlanner.Plan(localVersion, info.PredownloadVersion, info.PredownloadPatchSourceVersions);
        if (plan.Strategy == UpdateStrategy.Incremental)
        {
            var manifest = await channel.GetIncrementalManifestAsync(
                               server, plan.FromVersion, plan.ToVersion, cancellationToken)
                           ?? throw new UpdateException("Predownload incremental manifest is unavailable.");

            var incremental = new IncrementalUpdateService(downloader, patchApplier, logger);
            await incremental.PredownloadAsync(installDir, manifest, progress, cancellationToken);

            var totalBytes = manifest.Groups.Sum(g => g.PatchSize) + manifest.Files.Sum(f => f.Size);
            return new PredownloadSummary(plan.FromVersion, plan.ToVersion, totalBytes);
        }

        // 包式渠道（整包预下载）
        var packageManifest = await channel.GetPredownloadManifestAsync(server, cancellationToken)
                              ?? throw new UpdateException(
                                  $"Predownload version {info.PredownloadVersion} has no patch for local version {plan.FromVersion}; wait for the full update.");

        var packages = new PackageInstallerService(downloader, logger);
        await packages.PredownloadAsync(installDir, packageManifest, progress, cancellationToken);
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
            await packages.ApplyPredownloadAsync(installDir, staged, progress, cancellationToken);
            repaired = 0;
        }
        else
        {
            var incremental = new IncrementalUpdateService(downloader, patchApplier, logger);
            await incremental.ApplyAsync(installDir, staged, progress, cancellationToken);
            repaired = await RepairAgainstManifestAsync(
                installDir, server, channel, staged.Version, progress, cancellationToken);
        }

        await new LocalStateService(installDir).SaveAsync(
            new LocalGameState { GameId = game.Id, ServerId = server.Id, Version = staged.Version },
            cancellationToken);

        return new UpdateOutcome(
            staged.EntriesAreArchives ? UpdateStrategy.FullSync : UpdateStrategy.Incremental,
            "", staged.Version, repaired);
    }

    private async Task<int> UpdateFullAsync(
        string installDir,
        GameServer server,
        IGameChannelApi channel,
        UpdatePlan plan,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        var manifest = await channel.GetManifestAsync(server, plan.ToVersion, cancellationToken);

        if (manifest.EntriesAreArchives)
        {
            var packages = new PackageInstallerService(downloader, logger);
            await packages.InstallAsync(installDir, manifest, progress, cancellationToken);
            return 0;
        }

        var installer = new GameInstallService(downloader, options, logger);
        await installer.SyncAsync(installDir, manifest, progress, cancellationToken);
        return 0;
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
                           server, plan.FromVersion, plan.ToVersion, cancellationToken)
                       ?? throw new UpdateException("Incremental manifest unavailable; use the full update instead.");

        var incremental = new IncrementalUpdateService(downloader, patchApplier, logger);
        await incremental.PredownloadAsync(installDir, manifest, progress, cancellationToken);
        await incremental.ApplyAsync(installDir, manifest, progress, cancellationToken);

        return await RepairAgainstManifestAsync(
            installDir, server, channel, plan.ToVersion, progress, cancellationToken);
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
        var manifest = await channel.GetManifestAsync(server, version, cancellationToken);
        var before = ManifestVerifier.VerifyFast(installDir, manifest);
        if (before.IsComplete)
        {
            return 0;
        }

        var installer = new GameInstallService(downloader, options, logger);
        await installer.SyncAsync(installDir, manifest, progress, cancellationToken);
        return before.NeedsDownload.Count;
    }

    private static string? GetLocalVersion(LocalStateService state, GameDefinition game, GameServer server) =>
        state.Load(game.Id, server.Id)?.Version;
}
