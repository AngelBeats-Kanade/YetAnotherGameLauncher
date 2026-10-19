using Xunit;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Core.Utilities;
using YetAnotherGameLauncher.ViewModels;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 档位资源包下载（2026-10-19）与暂停/续传（2026-10-19）的 VM 语义：
/// 主按钮状态机（继续下载 / 下载资源包 / 立即更新 / 校验修复）、状态行优先级、
/// 服务器能力门控、显式清理其他档位（预览 → 确认 → 删除）、暂停保留产物与续传提示。
/// </summary>
[Collection("sequential")]
public class ResourceTierVmTests : IDisposable
{
    /// <summary>样例配置上注入档位与服务器档位索引选项（终末地条目保持原样，安装根替换照常生效）。</summary>
    private static string Config(string? tier, bool tierIndex)
    {
        var launch = tier is null ? "" : ", \"launch\": { \"resourceQualityTier\": \"" + tier + "\" }";
        var options = tierIndex
            ? ", \"options\": { \"resourcePackIndexUrl\": \"https://tier.example/index.json\" }"
            : "";
        return VmFactory.SampleConfigJson
            .Replace(
                "\"executable\": \"Client/Binaries/Win64/Client-Win64-Shipping.exe\",",
                "\"executable\": \"Client/Binaries/Win64/Client-Win64-Shipping.exe\"" + launch + ",")
            .Replace(
                "{ \"id\": \"cn\", \"name\": \"国服\" }",
                "{ \"id\": \"cn\", \"name\": \"国服\"" + options + " }");
    }

    private VmFactory.Context _ctx;

    public ResourceTierVmTests() => _ctx = VmFactory.Build(configJson: Config("uhd", tierIndex: true));

    public void Dispose() => _ctx.TempDir.Dispose();

    /// <summary>换一个配置重建上下文（旧上下文的临时目录先释放，避免泄漏）。</summary>
    private void NewContext(string configJson)
    {
        _ctx.TempDir.Dispose();
        _ctx = VmFactory.Build(configJson: configJson);
    }

    /// <summary>初始化主 VM 并写本地状态（已安装、已校验），配好渠道能力与清单；返回鸣潮条目。</summary>
    private async Task<YetAnotherGameLauncher.ViewModels.GameItemViewModel> SetupAsync(
        string version = "3.6.0", string? diskTier = "hd", bool supportsPacks = true)
    {
        _ctx.Kuro.SupportsResourcePacks = supportsPacks;
        _ctx.Kuro.VersionInfo = new ChannelVersionInfo { LatestVersion = version };
        _ctx.Kuro.Manifests[version] = new GameManifest
        {
            Version = version,
            Files = [],
            // 渠道会给清单盖"这次落盘是哪一档"的戳（Core 据此写状态）——测试夹具同形
            ResourceTier = diskTier ?? "hd",
        };
        await _ctx.Vm.InitializeAsync();
        var wuwa = _ctx.Vm.Games[0];
        wuwa.ResetVersionCheckCache(); // InitializeAsync 的刷新可能已缓存检测结果；随后改 fixture 必须清
        Directory.CreateDirectory(Path.Combine(wuwa.InstallDirPath, ".yagl"));
        File.WriteAllText(
            Path.Combine(wuwa.InstallDirPath, ".yagl", "state.json"),
            System.Text.Json.JsonSerializer.Serialize(
                new LocalGameState
                {
                    GameId = wuwa.Game.Id,
                    ServerId = wuwa.SelectedServer.Id,
                    Version = version,
                    ResourceTier = diskTier,
                }));
        return wuwa;
    }

    private static ResourcePackManifest Pack(string tier, params (string Path, long Size)[] files) =>
        new(tier, $"Client/Content/{tier.ToUpperInvariant()}", "3.6.0",
            [.. files.Select(f => new ManifestFile(f.Path, f.Size, "", Url: null))]);

    // ---------- 主按钮状态机与状态行 ----------

    [Fact]
    public async Task Refresh_DiskTierDiffersFromSelection_ShowsDownloadPackButtonAndStatus()
    {
        var wuwa = await SetupAsync(diskTier: "hd");

        await wuwa.RefreshAsync();

        Assert.True(wuwa.NeedsResourcePack);
        Assert.Equal("下载资源包", wuwa.InstallButtonText);
        Assert.Equal("已选超高清（UHD），资源包尚未下载", wuwa.StatusText);
    }

    [Fact]
    public async Task Refresh_TierSynced_ShowsVerifyAndNoPackPrompt()
    {
        var wuwa = await SetupAsync(diskTier: "uhd");

        await wuwa.RefreshAsync();

        Assert.False(wuwa.NeedsResourcePack);
        Assert.Equal("校验修复", wuwa.InstallButtonText);
        Assert.Equal("已是最新版本", wuwa.StatusText);
    }

    [Fact]
    public async Task Refresh_LegacyStateWithoutTier_AsksForPackDownload()
    {
        // 旧状态文件没有档位记录（null）：视为未同步 → 提示补包；点一次只补缺失文件
        var wuwa = await SetupAsync(diskTier: null);

        await wuwa.RefreshAsync();

        Assert.Equal("下载资源包", wuwa.InstallButtonText);
    }

    [Fact]
    public async Task Refresh_ServerWithoutTierSupport_TierStaysLaunchArgumentOnly()
    {
        // 国际服/B 服实测无档位索引：档位只作启动参数，不提示补包、不给清理入口
        var wuwa = await SetupAsync(diskTier: null, supportsPacks: false);

        await wuwa.RefreshAsync();

        Assert.False(wuwa.SupportsResourcePackDownload);
        Assert.False(wuwa.NeedsResourcePack);
        Assert.Equal("校验修复", wuwa.InstallButtonText);
    }

    [Fact]
    public async Task Refresh_UpdateAndMissingPack_PackPromptWinsOverUpdate()
    {
        var wuwa = await SetupAsync(version: "3.5.0", diskTier: "hd");
        _ctx.Kuro.VersionInfo = new ChannelVersionInfo { LatestVersion = "3.6.0" };
        _ctx.Kuro.Manifests["3.6.0"] = new GameManifest
        {
            Version = "3.6.0",
            Files = [],
            ResourceTier = "uhd",
        };
        wuwa.ResetVersionCheckCache(); // 改 fixture 后清会话缓存，否则读到 SetupAsync 时的旧检测

        await wuwa.RefreshAsync();

        Assert.True(wuwa.HasUpdate);
        Assert.Equal("下载资源包", wuwa.InstallButtonText); // 点它 = 更新 + 补包一体
        Assert.Equal("已选超高清（UHD），资源包尚未下载", wuwa.StatusText);
    }

    [Fact]
    public async Task InstallOrUpdate_MissingPack_DownloadsTierFilesAndRecordsTier()
    {
        var wuwa = await SetupAsync(diskTier: "hd");
        var packBytes = "uhd-pack"u8.ToArray();
        _ctx.Kuro.Manifests["3.6.0"] = new GameManifest
        {
            Version = "3.6.0",
            Files =
            [
                new ManifestFile(
                    "Client/Content/UHD/pak1.pak", packBytes.Length, Hashing.Md5Hex(packBytes),
                    Url: "https://cdn/uhd-pak1.pak"),
            ],
            ResourceTier = "uhd",
            PreservedPaths = ["Client/Content/HD", "Client/Content/SD"],
        };
        _ctx.Downloader.Responses["https://cdn/uhd-pak1.pak"] = packBytes;

        await wuwa.InstallOrUpdateCommand.ExecuteAsync(null);

        Assert.True(File.Exists(Path.Combine(wuwa.InstallDirPath, "Client", "Content", "UHD", "pak1.pak")));
        Assert.Equal(
            "uhd",
            new LocalStateService(wuwa.InstallDirPath).Load(wuwa.Game.Id, wuwa.SelectedServer.Id)!.ResourceTier);
        Assert.False(wuwa.NeedsResourcePack);
    }

    // ---------- 暂停 / 续传 ----------

    [Fact]
    public async Task Refresh_InterruptedDownloadMarker_ShowsContinueButtonAndStatus()
    {
        var wuwa = await SetupAsync(diskTier: "uhd");
        new DownloadSessionService(wuwa.InstallDirPath).Begin("update", "3.6.0");

        await wuwa.RefreshAsync();

        Assert.True(wuwa.HasInterruptedDownload);
        Assert.Equal("继续下载", wuwa.InstallButtonText);
        Assert.Equal("上次下载未完成：点「继续下载」从已下载处继续", wuwa.StatusText);
    }

    [Fact]
    public async Task PauseDownload_CancelsInFlightUpdateAndKeepsMarker()
    {
        var wuwa = await SetupAsync(diskTier: "uhd");
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ctx.Kuro.ManifestHandler = async (_, _, _, token) =>
        {
            reached.TrySetResult();
            await gate.Task.WaitAsync(token); // 暂停 → 取消令牌 → 抛 OCE（模拟下载中被停）
            return _ctx.Kuro.Manifests["3.6.0"];
        };

        var running = wuwa.InstallOrUpdateCommand.ExecuteAsync(null);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        wuwa.PauseDownloadCommand.Execute(null);
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        // 暂停语义：不报错、不写版本、标记留存（重开应用/点继续都能接上）
        Assert.False(wuwa.IsBusy);
        Assert.Equal("已暂停：已下载的部分会保留，点「继续下载」接着下", wuwa.StatusText);
        Assert.Equal("继续下载", wuwa.InstallButtonText);
        Assert.NotNull(new DownloadSessionService(wuwa.InstallDirPath).Load());
    }

    [Fact]
    public async Task Resume_SuccessfulUpdate_ClearsMarkerAndRestoresNormalButton()
    {
        var wuwa = await SetupAsync(diskTier: "uhd");
        new DownloadSessionService(wuwa.InstallDirPath).Begin("update", "3.6.0");
        await wuwa.RefreshAsync();
        Assert.Equal("继续下载", wuwa.InstallButtonText);

        await wuwa.InstallOrUpdateCommand.ExecuteAsync(null);

        Assert.False(wuwa.HasInterruptedDownload);
        Assert.Equal("校验修复", wuwa.InstallButtonText);
        Assert.Null(new DownloadSessionService(wuwa.InstallDirPath).Load());
    }

    [Fact]
    public async Task Resume_PausedPredownload_RunsPredownloadChainInsteadOfUpdate()
    {
        // 阻塞项（2026-10-19 review）：标记只记"有没有未完成操作"是不够的——预下载暂停后主按钮
        // 写着「继续下载」，若改走更新链会绕过"先暂存、确认后应用"的两段契约。分派必须回到
        // 被暂停的那个动作：预下载 → 预下载链。
        var wuwa = await SetupAsync(diskTier: "uhd");
        _ctx.Kuro.VersionInfo = new ChannelVersionInfo
        {
            LatestVersion = "3.7.0",
            PredownloadAvailable = true,
            PredownloadVersion = "3.7.0",
        };
        wuwa.ResetVersionCheckCache();
        var stageBytes = "staged"u8.ToArray();
        _ctx.Kuro.PredownloadManifest = new GameManifest
        {
            Version = "3.7.0",
            Files =
            [
                new ManifestFile("packages/game-0.zip", stageBytes.Length, Hashing.Md5Hex(stageBytes),
                    Url: "https://cdn/3.7.0/game-0.zip"),
            ],
        };
        _ctx.Downloader.Responses["https://cdn/3.7.0/game-0.zip"] = stageBytes;
        new DownloadSessionService(wuwa.InstallDirPath).Begin(
            DownloadSessionKind.Predownload, "3.7.0", wuwa.Game.Id, wuwa.SelectedServer.Id);
        await wuwa.RefreshAsync();
        Assert.Equal("继续下载", wuwa.InstallButtonText);

        await wuwa.InstallOrUpdateCommand.ExecuteAsync(null);

        Assert.NotEmpty(_ctx.Kuro.PredownloadManifestRequests); // 走的是预下载链
        Assert.Empty(_ctx.Kuro.ManifestRequests);              // 没有走更新链（更新链必拉全量清单）
        Assert.Equal(
            "3.6.0",
            new LocalStateService(wuwa.InstallDirPath).Load(wuwa.Game.Id, wuwa.SelectedServer.Id)!.Version);
        Assert.False(wuwa.HasInterruptedDownload); // 预下载成功收尾：标记清掉
        Assert.True(wuwa.HasStagedPredownload);
    }

    [Fact]
    public async Task Resume_PausedUpdateWithStagedContent_RunsApplyChain()
    {
        // 应用/收尾阶段被停（暂存清单还在场——提交后才清）：续跑必须回到应用链幂等续跑，
        // 而不是按"当前最新版"重规划更新（后者会重下整包差量并直接跳版本）。
        var wuwa = await SetupAsync(diskTier: "uhd");
        _ctx.Kuro.VersionInfo = new ChannelVersionInfo { LatestVersion = "3.7.2" };
        _ctx.Kuro.Manifests["3.7.1"] = new GameManifest { Version = "3.7.1", Files = [], ResourceTier = "uhd" };
        _ctx.Kuro.Manifests["3.7.2"] = new GameManifest { Version = "3.7.2", Files = [], ResourceTier = "uhd" };
        wuwa.ResetVersionCheckCache();
        var staging = IncrementalUpdateService.PredownloadDir(wuwa.InstallDirPath);
        Directory.CreateDirectory(staging);
        await IncrementalUpdateService.WriteStagedManifestAsync(
            staging, new GameManifest { Version = "3.7.1", Files = [], ResourceTier = "uhd" }, CancellationToken.None);
        new DownloadSessionService(wuwa.InstallDirPath).Begin(
            DownloadSessionKind.Update, "3.7.1", wuwa.Game.Id, wuwa.SelectedServer.Id);
        await wuwa.RefreshAsync();

        await wuwa.InstallOrUpdateCommand.ExecuteAsync(null);

        Assert.Equal(
            "3.7.1",
            new LocalStateService(wuwa.InstallDirPath).Load(wuwa.Game.Id, wuwa.SelectedServer.Id)!.Version);
        Assert.Empty(_ctx.Kuro.IncrementalManifestRequests); // 没有按最新版重规划增量
    }

    [Fact]
    public async Task Resume_PausedPredownload_WindowClosed_ClearsMarkerAndUnlocksMainButton()
    {
        // 2026-10-19 二审 N1（非阻塞，但会锁死主 CTA）：预下载暂停后窗口关闭——无条件回预下载链会
        // 每次点击都失败（Core 在写标记前抛"没有进行中的预下载"），标记永不清、预下载钮已隐藏，
        // 用户没有任何 UI 出路。修后：这一次点击失败但标记被清，主按钮回到常规语义（可点、有实效）
        var wuwa = await SetupAsync(diskTier: "uhd");
        _ctx.Kuro.VersionInfo = new ChannelVersionInfo { LatestVersion = "3.6.0" }; // 窗口已关：无预下载
        wuwa.ResetVersionCheckCache();
        new DownloadSessionService(wuwa.InstallDirPath).Begin(
            DownloadSessionKind.Predownload, "3.7.2", wuwa.Game.Id, wuwa.SelectedServer.Id);
        await wuwa.RefreshAsync();
        Assert.Equal("继续下载", wuwa.InstallButtonText);

        await wuwa.InstallOrUpdateCommand.ExecuteAsync(null);

        Assert.False(wuwa.HasInterruptedDownload);            // 不可继续的标记已被清
        Assert.Equal("校验修复", wuwa.InstallButtonText);      // 主按钮回到可执行的动作，而非锁死在失败动作上
        Assert.NotEqual("上次下载未完成：点「继续下载」从已下载处继续", wuwa.StatusText);
    }

    [Fact]
    public async Task Cleanup_Prepare_ListsTierThatOnlyHasTempLeftovers()
    {
        // 2026-10-19 二审 N3：「约 X」按可释放字节（含 .temp），档位列表也必须同口径——
        // 某一档只剩半成品时它既计入释放量就要出现在"将删除以下档位"里，否则文案自相矛盾
        var wuwa = await SetupAsync(diskTier: "uhd");
        _ctx.Kuro.ResourcePacks["uhd"] = Pack("uhd", ("Client/Content/UHD/pak.pak", 3));
        _ctx.Kuro.ResourcePacks["hd"] = Pack("hd", ("Client/Content/HD/pak.pak", 5));
        Directory.CreateDirectory(Path.Combine(wuwa.InstallDirPath, "Client", "Content", "HD"));
        File.WriteAllText(Path.Combine(wuwa.InstallDirPath, "Client", "Content", "HD", "pak.pak.temp"), "123");
        await wuwa.RefreshAsync();

        await wuwa.LaunchSettings.PrepareCleanupCommand.ExecuteAsync(null);

        Assert.True(wuwa.LaunchSettings.ShowCleanupConfirm);
        Assert.Contains("高清（HD）", wuwa.LaunchSettings.CleanupConfirmMessage);
        Assert.Contains("3\u00A0B", wuwa.LaunchSettings.CleanupConfirmMessage); // FormatBytes 数值与单位间用 NBSP
    }

    [Fact]
    public async Task Resume_MarkerBelongsToAnotherServer_NoResumePrompt()
    {
        // 同一安装目录被多服务器复用：别的服务器留下的标记不得在这台服务器上提示「继续下载」（2026-10-19 review）
        var wuwa = await SetupAsync(diskTier: "uhd");
        new DownloadSessionService(wuwa.InstallDirPath).Begin(
            DownloadSessionKind.Update, "3.6.0", wuwa.Game.Id, "global");

        await wuwa.RefreshAsync();

        Assert.False(wuwa.HasInterruptedDownload);
        Assert.Equal("校验修复", wuwa.InstallButtonText);
    }

    [Fact]
    public async Task PauseButton_HiddenWhenBusyStateHasNoPausableOperation()
    {
        // 死控件（2026-10-19 review）：启动/登记/拉清单等忙碌态没有取消源，暂停钮必须隐藏
        var wuwa = await SetupAsync(diskTier: "uhd");
        await wuwa.RefreshAsync();

        wuwa.IsBusy = true; // 模拟无取消源的忙碌态（Core 侧拉起被占）
        Assert.False(wuwa.CanPauseDownload);

        wuwa.IsBusy = false;
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ctx.Kuro.ManifestHandler = async (_, _, _, token) =>
        {
            reached.TrySetResult();
            await gate.Task.WaitAsync(token);
            return _ctx.Kuro.Manifests["3.6.0"];
        };
        var running = wuwa.InstallOrUpdateCommand.ExecuteAsync(null);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(wuwa.CanPauseDownload); // 更新链挂了取消源：按钮在场

        wuwa.PauseDownloadCommand.Execute(null);
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(wuwa.CanPauseDownload);
    }

    [Fact]
    public async Task BusyStateChange_NotifiesInstallButtonText()
    {
        // nit（2026-10-19 review）：忙碌态翻转时按钮文案必须重发通知，否则续传开始后界面仍写着「继续下载」
        var wuwa = await SetupAsync(diskTier: "uhd");
        new DownloadSessionService(wuwa.InstallDirPath).Begin(
            DownloadSessionKind.Update, "3.6.0", wuwa.Game.Id, wuwa.SelectedServer.Id);
        await wuwa.RefreshAsync();
        var raised = new List<string?>();
        wuwa.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        wuwa.IsBusy = true;

        Assert.Contains(nameof(GameItemViewModel.InstallButtonText), raised);
    }

    [Fact]
    public async Task SidebarStatus_UsesShortVariant_WhenDownloadInterrupted()
    {
        // nit（2026-10-19 review）：长状态在侧栏窄槽里被 CharacterEllipsis 截成残句，须给 Short 变体
        var wuwa = await SetupAsync(diskTier: "uhd");
        new DownloadSessionService(wuwa.InstallDirPath).Begin(
            DownloadSessionKind.Update, "3.6.0", wuwa.Game.Id, wuwa.SelectedServer.Id);

        await wuwa.RefreshAsync();

        Assert.Equal("上次下载未完成", wuwa.SidebarStatusText);
        Assert.NotEqual(wuwa.StatusText, wuwa.SidebarStatusText);
    }

    [Fact]
    public async Task SidebarStatus_UsesShortVariant_WhenTierPackMissing()
    {
        // 2026-10-19 judge 实锤（P3）：缺包长状态含档位名（"已选超高清（UHD），资源包尚未下载"）在侧栏
        // 窄槽里被 CharacterEllipsis 截成"已选超高清（UHD），资…"——与续传态同族，须给 Short 变体
        var wuwa = await SetupAsync(diskTier: "hd");

        await wuwa.RefreshAsync();

        Assert.True(wuwa.NeedsResourcePack);
        Assert.Equal("资源包待下载", wuwa.SidebarStatusText);
        Assert.NotEqual(wuwa.StatusText, wuwa.SidebarStatusText);
    }

    [Fact]
    public async Task Cleanup_Prepare_WarnsWhenSelectedTierOnlyPartiallyDownloaded()
    {
        // 破坏性安全网（2026-10-19 review）：选中档位只下了一部分同样会在清理后缺档——
        // 警示口径是"未完整落盘"，不是"一个文件都没有"
        var wuwa = await SetupAsync(diskTier: "uhd");
        _ctx.Kuro.ResourcePacks["uhd"] = Pack(
            "uhd", ("Client/Content/UHD/pak1.pak", 3), ("Client/Content/UHD/pak2.pak", 3));
        _ctx.Kuro.ResourcePacks["hd"] = Pack("hd", ("Client/Content/HD/pak.pak", 5));
        Directory.CreateDirectory(Path.Combine(wuwa.InstallDirPath, "Client", "Content", "UHD"));
        File.WriteAllText(Path.Combine(wuwa.InstallDirPath, "Client", "Content", "UHD", "pak1.pak"), "uhd");
        Directory.CreateDirectory(Path.Combine(wuwa.InstallDirPath, "Client", "Content", "HD"));
        File.WriteAllText(Path.Combine(wuwa.InstallDirPath, "Client", "Content", "HD", "pak.pak"), "12345");
        await wuwa.RefreshAsync();

        await wuwa.LaunchSettings.PrepareCleanupCommand.ExecuteAsync(null);

        Assert.True(wuwa.LaunchSettings.ShowCleanupConfirm);
        Assert.Contains("尚未下载完整", wuwa.LaunchSettings.CleanupConfirmMessage);
    }

    // ---------- 清理其他档位（显式动作） ----------

    [Fact]
    public async Task Cleanup_Prepare_ShowsSizesAndConfirm_ThenDeletesOtherTiers()
    {
        var wuwa = await SetupAsync(diskTier: "uhd");
        _ctx.Kuro.ResourcePacks["hd"] = Pack("hd", ("Client/Content/HD/pak.pak", 5));
        _ctx.Kuro.ResourcePacks["sd"] = Pack("sd", ("Client/Content/SD/pak.pak", 3));
        // 选中档位的包清单也要给出：预告文案靠它判定"选中档位是否已落盘"
        _ctx.Kuro.ResourcePacks["uhd"] = Pack("uhd", ("Client/Content/UHD/pak.pak", 3));
        var hdFile = Path.Combine(wuwa.InstallDirPath, "Client", "Content", "HD", "pak.pak");
        var sdFile = Path.Combine(wuwa.InstallDirPath, "Client", "Content", "SD", "pak.pak");
        var uhdFile = Path.Combine(wuwa.InstallDirPath, "Client", "Content", "UHD", "pak.pak");
        Directory.CreateDirectory(Path.GetDirectoryName(hdFile)!);
        Directory.CreateDirectory(Path.GetDirectoryName(sdFile)!);
        Directory.CreateDirectory(Path.GetDirectoryName(uhdFile)!);
        File.WriteAllText(hdFile, "12345");
        File.WriteAllText(sdFile, "123");
        File.WriteAllText(uhdFile, "uhd"); // 选中档位有文件 → 不附加"未落盘"警示
        await wuwa.RefreshAsync();

        await wuwa.LaunchSettings.PrepareCleanupCommand.ExecuteAsync(null);

        Assert.True(wuwa.LaunchSettings.ShowCleanupConfirm);
        Assert.Contains("高清（HD）", wuwa.LaunchSettings.CleanupConfirmMessage);
        Assert.Contains("标准（SD）", wuwa.LaunchSettings.CleanupConfirmMessage);
        Assert.DoesNotContain("注意", wuwa.LaunchSettings.CleanupConfirmMessage);
        Assert.True(File.Exists(hdFile), "预览阶段不得删任何文件");

        await wuwa.LaunchSettings.ConfirmCleanupCommand.ExecuteAsync(null);

        Assert.False(File.Exists(hdFile));
        Assert.False(File.Exists(sdFile));
        Assert.True(File.Exists(uhdFile), "选中档位不得被清理");
        Assert.Contains("已清理", wuwa.LaunchSettings.PackFeedback.Message);
    }

    [Fact]
    public async Task Cleanup_Prepare_WarnsWhenSelectedTierNotOnDisk()
    {
        // 切档后立刻清理（不要求先下载新档）：允许，但必须警示"补下前可能无法运行"
        var wuwa = await SetupAsync(diskTier: "hd");
        _ctx.Kuro.ResourcePacks["hd"] = Pack("hd", ("Client/Content/HD/pak.pak", 5));
        // 选中档位（uhd）的包清单必须可核——否则落盘状态"未知"，警示口径退化为清单取不到
        _ctx.Kuro.ResourcePacks["uhd"] = Pack("uhd", ("Client/Content/UHD/pak.pak", 3));
        Directory.CreateDirectory(Path.Combine(wuwa.InstallDirPath, "Client", "Content", "HD"));
        File.WriteAllText(Path.Combine(wuwa.InstallDirPath, "Client", "Content", "HD", "pak.pak"), "12345");
        await wuwa.RefreshAsync();

        await wuwa.LaunchSettings.PrepareCleanupCommand.ExecuteAsync(null);

        Assert.True(wuwa.LaunchSettings.ShowCleanupConfirm);
        Assert.Contains("注意", wuwa.LaunchSettings.CleanupConfirmMessage);
    }

    [Fact]
    public async Task Cleanup_Prepare_NothingToClean_NoConfirmOverlay()
    {
        var wuwa = await SetupAsync(diskTier: "uhd");
        _ctx.Kuro.ResourcePacks["hd"] = Pack("hd", ("Client/Content/HD/pak.pak", 5));
        await wuwa.RefreshAsync();

        await wuwa.LaunchSettings.PrepareCleanupCommand.ExecuteAsync(null);

        Assert.False(wuwa.LaunchSettings.ShowCleanupConfirm);
        // 无可清理时不谎报"一切正常"：未登记的档位清单取不到，如实附说明（2026-10-19 review）
        Assert.StartsWith("其它档位没有可清理的本地资源文件", wuwa.LaunchSettings.PackFeedback.Message);
        Assert.Contains("的清单取不到，本次未清理", wuwa.LaunchSettings.PackFeedback.Message);
    }

    [Fact]
    public async Task Cleanup_DirtyDraft_DisablesEntry()
    {
        // 草稿有未保存变更时"保留哪一档"有歧义：清理入口禁用（先保存再清理）
        var wuwa = await SetupAsync(diskTier: "uhd");
        await wuwa.RefreshAsync();
        Assert.True(wuwa.LaunchSettings.CanCleanupOtherTiers);

        wuwa.LaunchSettings.SelectedResourceQuality =
            wuwa.LaunchSettings.ResourceQualities.First(q => q.Tier == "sd");

        Assert.False(wuwa.LaunchSettings.CanCleanupOtherTiers);
    }

    // ---------- 保存档位后的提示与按钮联动 ----------

    [Fact]
    public async Task SaveTier_RefreshesHintAndButtonWithoutAutoDownload()
    {
        // 用户确认的语义：保存档位不自动下载（保存槽给指引 + 主按钮变「下载资源包」）
        NewContext(Config(null, tierIndex: true));
        _ctx.Downloader.Requests.Clear();
        var wuwa = await SetupAsync(diskTier: "hd");
        await wuwa.RefreshAsync();
        Assert.Equal("校验修复", wuwa.InstallButtonText);

        wuwa.LaunchSettings.SelectedResourceQuality =
            wuwa.LaunchSettings.ResourceQualities.First(q => q.Tier == "uhd");
        await wuwa.LaunchSettings.SaveCommand.ExecuteAsync(null);

        Assert.Contains("资源包尚未下载", wuwa.LaunchSettings.ResourceTierHint);
        Assert.Equal("下载资源包", wuwa.InstallButtonText);
        Assert.Empty(_ctx.Downloader.Requests); // 保存不触发任何下载
    }

    [Fact]
    public async Task SettingsHint_RecomputedAfterHostStateChanges()
    {
        // 2026-10-19 review：说明行只在"进设置页/保存/清理"时算是不够的——服务器能力与安装态由
        // RefreshAsync 结算，结算完必须同轮刷新已建的说明行（否则停在上一轮服务器/能力的提示上）
        var wuwa = await SetupAsync(diskTier: "hd", supportsPacks: false);
        await wuwa.RefreshAsync();
        wuwa.LaunchSettings.RefreshResourceTierHint(); // 等价于首次进设置页（页面 VM 首访即算）
        Assert.Equal(
            "该服务器不支持按档位下载资源包：档位仅作为启动参数传给游戏。",
            wuwa.LaunchSettings.ResourceTierHint);

        _ctx.Kuro.SupportsResourcePacks = true; // 换到支持档位的服务器（或能力结算晚到）
        await wuwa.RefreshAsync();

        Assert.Contains("资源包尚未下载", wuwa.LaunchSettings.ResourceTierHint);

        // 离线分支（VersionInfoError 走 catch 兜底）同样要收敛：切服后断网不该停在上一台服务器的提示
        _ctx.Kuro.SupportsResourcePacks = false;
        _ctx.Kuro.VersionInfoError = new UpdateException("offline");
        wuwa.ResetVersionCheckCache();
        await wuwa.RefreshAsync();

        Assert.Equal(
            "该服务器不支持按档位下载资源包：档位仅作为启动参数传给游戏。",
            wuwa.LaunchSettings.ResourceTierHint);
        Assert.Equal("无法连接服务器，版本信息不可用", wuwa.StatusText); // 证明确实走的离线兜底分支，而非在线路径
    }

    [Fact]
    public async Task SettingsHint_ServerWithoutTierSupport_SaysLaunchArgumentOnly()
    {
        NewContext(Config("uhd", tierIndex: false));
        var wuwa = await SetupAsync(diskTier: "hd", supportsPacks: false);
        await wuwa.RefreshAsync();

        wuwa.LaunchSettings.RefreshResourceTierHint();

        Assert.Equal("该服务器不支持按档位下载资源包：档位仅作为启动参数传给游戏。", wuwa.LaunchSettings.ResourceTierHint);
    }
}
