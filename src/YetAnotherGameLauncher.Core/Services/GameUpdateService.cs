using System.Text.Json;
using Microsoft.Extensions.Logging;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Utilities;

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
    ILogger? logger = null,
    IFilePatchApplier? filePatchApplier = null)
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

        // 包式渠道：优先预下载 v2 增量载荷（8.72 GiB 替代整包数十 GiB，2026-10-17 立案"预下载链未接增量"）：
        // 计划不可得/下载失败/载荷不可用则回退整包预下载（整包永远可用）
        if (filePatchApplier is not null)
        {
            var deltaPlan = await channel.GetDeltaPackageAsync(server, plan.FromVersion, plan.ToVersion, cancellationToken)
                .ConfigureAwait(false);
            if (deltaPlan is not null)
            {
                try
                {
                    var applier = new DeltaPackageApplier(downloader, filePatchApplier, logger);
                    var bytes = await applier.StagePayloadAsync(installDir, deltaPlan, progress, cancellationToken)
                        .ConfigureAwait(false);
                    await WriteStagedDeltaAsync(installDir, deltaPlan, cancellationToken).ConfigureAwait(false);
                    // 上轮整包预下载的暂存（.yagl/predownload/packages）已被增量取代：留着重占数十 GiB
                    if (FileUtilities.TryDeleteDirectory(PackageInstallerService.PackagesDir(installDir), logger))
                    {
                        logger?.LogInformation("Removed the superseded full-package predownload staging.");
                    }

                    logger?.LogInformation(
                        "Delta predownload staged: {Bytes} bytes for {From} -> {To}.", bytes, plan.FromVersion, plan.ToVersion);
                    progress?.Report(new UpdateProgress(
                        UpdatePhase.Done, 0, 0, deltaPlan.Volumes.Count, deltaPlan.Volumes.Count, null));
                    return new PredownloadSummary(plan.FromVersion, plan.ToVersion, bytes);
                }
                catch (Exception ex) when (ex is DeltaPackageNotApplicableException or DownloadException)
                {
                    // 载荷下载/形态问题：只清增量侧半成品，回退整包预下载（与直接更新链同哲学）。
                    // 不整删 .yagl/predownload（复核 P1，2026-10-18）：其中的 packages/ 是上轮整包
                    // 预下载的暂存，回退路径按 size/MD5 复用它——整删会让用户零下载可得的内容重下数十 GiB
                    logger?.LogWarning(ex, "Delta predownload failed; falling back to the full package predownload.");
                    ClearStagedDeltaSide(installDir);
                }
            }
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

        if (staged.StagedAsDeltaPackage)
        {
            // v2 增量预下载（2026-10-18 接线）：应用预置载荷；不适用则清掉后走常规更新流程
            if (await TryApplyStagedDeltaAsync(installDir, staged, progress, cancellationToken).ConfigureAwait(false))
            {
                await new LocalStateService(installDir).SaveAsync(
                    new LocalGameState { GameId = game.Id, ServerId = server.Id, Version = staged.Version },
                    cancellationToken).ConfigureAwait(false);
                return new UpdateOutcome(UpdateStrategy.FullSync, "", staged.Version, 0);
            }

            // 暂存增量已不适用：常规更新会重探当前最新版本（内部再试增量、必要时回退整包），
            // 避免把"下载时的目标版本"当成"当前最新"落盘（服务器可能已前移）
            logger?.LogInformation(
                "Staged delta predownload is no longer applicable; running the regular update flow.");
            return await UpdateAsync(installDir, game, server, channel, progress, cancellationToken).ConfigureAwait(false);
        }

        var strategy = staged.EntriesAreArchives ? UpdateStrategy.FullSync : UpdateStrategy.Incremental;
        int repaired;
        if (staged.EntriesAreArchives)
        {
            // 应用前的本地版本即清理清单的起点（落盘版本在下方才更新）
            var applyFrom = GetLocalVersion(new LocalStateService(installDir), game, server) ?? "";
            var packages = new PackageInstallerService(downloader, logger);
            await packages.ApplyPredownloadAsync(
                installDir, staged, progress, cancellationToken,
                await TryGetObsoleteFilesAsync(channel, server, applyFrom, staged.Version, cancellationToken)
                    .ConfigureAwait(false)).ConfigureAwait(false);
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

        return new UpdateOutcome(strategy, "", staged.Version, repaired);
    }

    /// <summary>
    /// 应用预下载的 v2 增量计划（2026-10-18）：成功 true（清单/计划暂存清理，载荷由应用器清理）；
    /// 计划缺失/载荷不可用/装配无单文件补丁器 → false（调用方回退整包）；补丁工具不可用 →
    /// 抛带"载荷已保留"指引的可重试错误（与直接更新链同语义，绝不静默丢载荷）。
    /// </summary>
    private async Task<bool> TryApplyStagedDeltaAsync(
        string installDir,
        GameManifest staged,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        var plan = TryLoadStagedDeltaPlan(installDir);
        if (filePatchApplier is null || plan is null)
        {
            logger?.LogWarning(
                "Staged delta plan is missing or the single-file patch applier is not wired; falling back to the full package.");
            ClearStagedDelta(installDir);
            return false;
        }

        try
        {
            var applier = new DeltaPackageApplier(downloader, filePatchApplier, logger);
            await applier.ApplyAsync(installDir, plan, progress, cancellationToken).ConfigureAwait(false);
            ClearStagedDelta(installDir);
            logger?.LogInformation(
                "Staged delta package applied for {From} -> {To}.", plan.FromVersion, staged.Version);
            return true;
        }
        catch (DeltaPackageSupplyException ex)
        {
            logger?.LogWarning(ex, "Patch tool is unavailable; keeping the staged delta payload for a later retry.");
            throw new UpdateException(
                $"{ex.Message} The downloaded delta payload is kept; fix the patch tool and retry the update.", ex);
        }
        catch (DeltaPackageNotApplicableException ex)
        {
            logger?.LogWarning(ex, "Staged delta package is not applicable; falling back to the full package.");
            ClearStagedDelta(installDir);
            return false;
        }
    }

    /// <summary>预下载的 v2 增量计划文件名（与暂存清单同目录）。</summary>
    internal const string StagedDeltaPlanFileName = "delta.json";

    /// <summary>写暂存增量计划与标记清单（应用阶段据此识别"增量预下载"）。</summary>
    private static async Task WriteStagedDeltaAsync(
        string installDir, DeltaPackagePlan plan, CancellationToken cancellationToken)
    {
        var staging = IncrementalUpdateService.PredownloadDir(installDir);
        Directory.CreateDirectory(staging);
        await FileUtilities.WriteAtomicAsync(
            Path.Combine(staging, StagedDeltaPlanFileName),
            JsonSerializer.Serialize(plan, Json.Default),
            cancellationToken).ConfigureAwait(false);
        await IncrementalUpdateService.WriteStagedManifestAsync(
            staging,
            new GameManifest
            {
                Version = plan.ToVersion,
                EntriesAreArchives = true,
                StagedAsDeltaPackage = true,
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取预下载的增量计划；没有/损坏返回 null（按"暂存未知"处理）。</summary>
    internal static DeltaPackagePlan? TryLoadStagedDeltaPlan(string installDir)
    {
        var path = Path.Combine(IncrementalUpdateService.PredownloadDir(installDir), StagedDeltaPlanFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DeltaPackagePlan>(File.ReadAllText(path), Json.Default);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>清掉增量预下载的全部暂存（载荷分卷 + 计划 + 标记清单）。</summary>
    private void ClearStagedDelta(string installDir)
    {
        FileUtilities.TryDeleteDirectory(DeltaPackageApplier.PayloadDir(installDir), logger);
        FileUtilities.TryDeleteDirectory(IncrementalUpdateService.PredownloadDir(installDir), logger);
    }

    /// <summary>
    /// 回退整包前的增量侧清理（复核 P1，2026-10-18）：载荷分卷 + 计划（delta.json）+ 增量标记清单；
    /// **保留** .yagl/predownload/packages——那是整包预下载的暂存，回退路径要按 size/MD5 复用
    /// （整删预下载目录会把数十 GiB 已暂存整包作废，用户零下载可得的内容重下）。
    /// 标记清单只在它确实是增量形态时删：包式整包预下载写的是同一个 manifest.json。
    /// </summary>
    private void ClearStagedDeltaSide(string installDir)
    {
        FileUtilities.TryDeleteDirectory(DeltaPackageApplier.PayloadDir(installDir), logger);
        var staging = IncrementalUpdateService.PredownloadDir(installDir);
        FileUtilities.DeleteQuiet(Path.Combine(staging, StagedDeltaPlanFileName));
        if (IncrementalUpdateService.TryLoadStagedManifest(installDir) is { StagedAsDeltaPackage: true })
        {
            FileUtilities.DeleteQuiet(Path.Combine(staging, IncrementalUpdateService.StagedManifestFileName));
        }
    }

    /// <summary>组级回退的产物直链解析缝：懒取目标版本全量清单建 相对路径→URL 映射，仅在首个
    /// 差分源缺失触发回退时才联网取全量清单（happy path 零额外请求）。协议实证（2026-10-02 真机）：
    /// 官方增量 dstFiles 原始条目无 url 字段，但组 dst 路径被全量清单全覆盖，且
    /// {resourcesBasePath}/{dest} 直链实测可下、md5 与增量 dstFiles 逐字符一致。全量清单抓取失败
    /// 原地抛出，由组回退链折算为可操作报错（携带根因）——此处吞异常折 null 会伪装成
    /// 「查无此路径」误导用户（review RF-B）。真查无路径返回 null；懒任务记住
    /// 首个取消令牌（单次 apply 内单一令牌，无跨令牌复用面）。</summary>
    /// <remarks>版本守卫（RF-E，2026-10-03）：Kuro 渠道的 GetManifestAsync 忽略请求版本、恒返回
    /// 服务器当前版本内容，本缝是它唯一无守卫的消费方——服务器版本与请求版本不一致时，回退会
    /// 拿错版本内容打旧版 dst 的 md5，报误导性校验失败。取清单前先核对服务器当前版本，不一致
    /// 抛带版本号的 UpdateException（经组回退链折算，根因可见）。</remarks>
    private static Func<string, CancellationToken, Task<string?>> CreateDstUrlResolver(
        GameServer server, IGameChannelApi channel, string version)
    {
        Task<IReadOnlyDictionary<string, string>>? mapTask = null;
        return async (path, cancellationToken) =>
        {
            mapTask ??= LoadDstUrlMapAsync(server, channel, version, cancellationToken);
            var map = await mapTask.ConfigureAwait(false);
            return map.GetValueOrDefault(path);
        };

        static async Task<IReadOnlyDictionary<string, string>> LoadDstUrlMapAsync(
            GameServer server, IGameChannelApi channel, string version, CancellationToken cancellationToken)
        {
            var info = await channel.GetVersionInfoAsync(server, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(info.LatestVersion, version, StringComparison.Ordinal))
            {
                throw new UpdateException(
                    $"Server now serves version {info.LatestVersion}, so no direct downloads can be resolved " +
                    $"for version {version}. Re-check for updates (or re-run the predownload) and retry.");
            }

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
            // 增量优先（终末地 v2：8.72 GiB 载荷 vs 整包数十 GiB）；不适用则回退整包
            if (await TryApplyDeltaPackageAsync(installDir, server, channel, plan, progress, cancellationToken)
                    .ConfigureAwait(false))
            {
                return 0;
            }

            var packages = new PackageInstallerService(downloader, logger);
            await packages.InstallAsync(
                installDir, manifest, progress, cancellationToken,
                await TryGetObsoleteFilesAsync(channel, server, plan.FromVersion, plan.ToVersion, cancellationToken)
                    .ConfigureAwait(false)).ConfigureAwait(false);
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
        await incremental.ApplyAsync(
            installDir, manifest, progress, cancellationToken,
            dstUrlResolver: CreateDstUrlResolver(server, channel, plan.ToVersion)).ConfigureAwait(false);

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

    /// <summary>
    /// 优先走 v2 增量载荷：渠道给不出计划（含未注入单文件补丁器的装配）、计划不适用于当前安装
    /// （基文件缺失/内容与官方清单不符）或载荷自身不可用（条目读不出/产出与官方清单不符，
    /// <see cref="DeltaPackageUnusableException"/>，按 Warning 记录）时返回 false 由调用方回退
    /// 整包——回退时清掉载荷暂存（已经没用，留着白占数 GiB；供给失败例外，见下）。本地 IO/路径越界等真错误照常抛出。
    /// 例外（2026-10-18）：<see cref="DeltaPackageSupplyException"/> = 补丁工具/供给不可用——载荷是好的，
    /// <b>不清</b>载荷暂存、也不回退整包，改抛带"载荷已保留"指引的 <see cref="UpdateException"/>，
    /// 用户修复工具后重试即可零重下。
    /// </summary>
    private async Task<bool> TryApplyDeltaPackageAsync(
        string installDir,
        GameServer server,
        IGameChannelApi channel,
        UpdatePlan plan,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (filePatchApplier is null)
        {
            return false;
        }

        var deltaPlan = await channel.GetDeltaPackageAsync(server, plan.FromVersion, plan.ToVersion, cancellationToken)
            .ConfigureAwait(false);
        if (deltaPlan is null)
        {
            return false;
        }

        try
        {
            var applier = new DeltaPackageApplier(downloader, filePatchApplier, logger);
            await applier.ApplyAsync(installDir, deltaPlan, progress, cancellationToken).ConfigureAwait(false);

            // 增量成功后，整包安装路径的暂存（.yagl/packages，实测可达数十 GiB）已无用：它服务
            // 直接安装/整包回退（InstallAsync）的重跑复用，而本次版本已由增量落地，留着只占盘
            // （2026-10-17 用户机实测 58 GiB）。注意只碰这个直接安装暂存目录——
            // 预下载内容在 .yagl/predownload（含 packages 子目录），不得触碰
            var installPackages = Path.Combine(installDir, LocalStateService.StateDirName, "packages");
            if (Directory.Exists(installPackages))
            {
                logger?.LogInformation("Removing the now-useless full-package staging after a successful delta apply.");
                if (!FileUtilities.TryDeleteDirectory(installPackages, logger))
                {
                    logger?.LogWarning("Full-package staging could not be removed: {Path}", installPackages);
                }
            }

            logger?.LogInformation("Delta package applied for {From} -> {To}.", plan.FromVersion, plan.ToVersion);
            return true;
        }
        catch (DeltaPackageSupplyException ex)
        {
            // 补丁工具/供给侧失败：载荷本身是好的——保留载荷暂存（修复工具后重试零重下），
            // 也不静默回退整包（整包会白费这份载荷，且工具问题同样可能挡住后续链）
            logger?.LogWarning(ex, "Patch tool is unavailable; keeping the staged delta payload for a later retry.");
            throw new UpdateException(
                $"{ex.Message} The downloaded delta payload is kept; fix the patch tool and retry the update.", ex);
        }
        catch (DeltaPackageNotApplicableException ex)
        {
            // "载荷不可用"（条目读不出/产出与清单不符）是异常信号，按 Warning 记以便诊断；
            // "安装不适用"（基文件被改/缺失）是预期形态，记 Information
            if (ex is DeltaPackageUnusableException)
            {
                logger?.LogWarning(ex, "Delta payload is unusable; falling back to the full package.");
            }
            else
            {
                logger?.LogInformation(ex, "Delta package not applicable; falling back to the full package.");
            }

            var payloadDir = DeltaPackageApplier.PayloadDir(installDir);
            if (Directory.Exists(payloadDir) && !FileUtilities.TryDeleteDirectory(payloadDir, logger))
            {
                logger?.LogWarning("Delta payload staging could not be removed: {Path}", payloadDir);
            }

            return false;
        }
    }

    /// <summary>
    /// 官方废弃文件清单（包式更新后的旧版残留清理；终末地实测 47.72 GiB）：渠道不支持
    /// （默认实现返回 null）或拉取失败都折算 null——清不掉残留不该让更新失败，失败原因
    /// 记 Warning 保持可诊断（更新链自身的错误另有出处，不会在这里被吞）。取消照常传播。
    /// </summary>
    private async Task<IReadOnlyList<string>?> TryGetObsoleteFilesAsync(
        IGameChannelApi channel,
        GameServer server,
        string fromVersion,
        string toVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            return await channel.GetObsoleteFilesAsync(server, fromVersion, toVersion, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(
                ex, "Obsolete file list unavailable for {From} -> {To}; stale files will not be cleaned.", fromVersion, toVersion);
            return null;
        }
    }

    private static string? GetLocalVersion(LocalStateService state, GameDefinition game, GameServer server) =>
        state.Load(game.Id, server.Id)?.Version;
}
