using Microsoft.Extensions.Logging;
using Xunit;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Core.Services.Umu;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Core.Tests.Services.Umu;

/// <summary>NativeUmuLauncher 计划组装：FakeProcessRunner 断言最终 FileName/Args/Env。</summary>
public sealed class NativeUmuLauncherLaunchTests : IDisposable
{
    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void BuildPlan_EmitsContainerEntryAndExpandedEnv()
    {
        if (!OperatingSystem.IsLinux())
        {
            // BuildPlan 在非 Linux 上直接抛错；Windows CI 只验证 BuildEntryCommand 纯逻辑
            var argv = BuildSampleEntry();
            Assert.EndsWith("_v2-entry-point", argv[0].Replace('\\', '/'));
            return;
        }

        var runner = new FakeProcessRunner();
        var launcher = new NativeUmuLauncher(runner, provisioner: null, dataHome: _temp.Path);
        var protonDir = CreateFakeProton();
        var runtimeDir = UmuPaths.RuntimeDirectory("steamrt4", _temp.Path);
        Directory.CreateDirectory(runtimeDir);
        File.WriteAllText(Path.Combine(runtimeDir, "_v2-entry-point"), "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(runtimeDir, UmuPaths.InstallMarkerName), "ok");

        var install = _temp.FilePath("game");
        Directory.CreateDirectory(install);
        var exe = Path.Combine(install, "Game.exe");
        File.WriteAllText(exe, "x");

        var manifest = ToolManifest.Load(protonDir);
        var plan = launcher.BuildPlan(
            "wuthering-waves", install, "Game.exe", protonDir, manifest,
            SteamRuntimeCatalog.Default,
            extraEnvironment: new Dictionary<string, string>
            {
                ["CUSTOM"] = "{installDir}/data",
                // 配置里的 PROTONPATH 是发行版代号（DW-Proton 等），不得覆盖已解析的绝对路径
                ["PROTONPATH"] = "DW-Proton",
            },
            dataHomeOverride: _temp.Path,
            umuId: "umu-3513350");

        Assert.EndsWith("_v2-entry-point", plan.FileName.Replace('\\', '/'));
        Assert.Contains("--verb=waitforexitandrun", plan.Arguments, StringComparison.Ordinal);
        Assert.Contains(exe, plan.Arguments, StringComparison.Ordinal);
        Assert.Equal(install, plan.WorkingDirectory);
        Assert.Equal("umu-3513350", plan.Environment["GAMEID"]);
        Assert.Equal("umu-3513350", plan.Environment["UMU_ID"]);
        Assert.Equal(protonDir, Path.GetFullPath(plan.Environment["PROTONPATH"]));
        Assert.Equal(Path.Combine(install, "data"), plan.Environment["CUSTOM"]);
        // prefix 仍按游戏 id 定位（umuId 覆盖不影响存量 prefix）
        Assert.Equal(
            CompatTools.PrefixPathFor("wuthering-waves", _temp.Path),
            plan.Environment["WINEPREFIX"]);
    }

    [Fact]
    public void BuildPlan_LogsResolvedCommandAtDebugLevel()
    {
        // 2026-10-02 用户需求：与 GameLauncherService 同型——Debug 级输出全量启动面（entry exe +
        // 全部参数 + 工作目录 + 环境变量）；Release 经组合根级别过滤不输出。workdir 断言带标签：
        // install 目录是 exe 参数路径的前缀，不带标签即死分支假绿
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("BuildPlan 仅 Linux（由 Linux 腿覆盖）");
        }

        var logger = new CapturingLogger();
        var plan = BuildPlanWithExtraEnvironment(
            new Dictionary<string, string> { ["CUSTOM"] = "custom-value" },
            umuId: null,
            logger: logger);

        var debug = Assert.Single(logger.Entries, e => e.Level == LogLevel.Debug);
        Assert.Contains(plan.FileName, debug.Message, StringComparison.Ordinal);
        Assert.Contains("--verb=waitforexitandrun", debug.Message, StringComparison.Ordinal);
        Assert.Contains($"workdir={plan.WorkingDirectory}", debug.Message, StringComparison.Ordinal);
        Assert.Contains("UMU_ID=", debug.Message, StringComparison.Ordinal);
        Assert.Contains("CUSTOM=custom-value", debug.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPlan_UmuIdSet_LegacyEnvUmuIdDoesNotOverride()
    {
        // 2026-09-28 review P3：launch.umuId 显式设置时为 UMU_ID/GAMEID 权威值——配置 env 里的
        // 同名键（首运托管残留/存量迁移遗留）不得反超。变异核对：去掉 BuildPlan 守卫本用例即红
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("BuildPlan 仅 Linux（守卫为纯字典逻辑，由 Linux 腿覆盖）");
        }

        var plan = BuildPlanWithExtraEnvironment(
            new Dictionary<string, string>
            {
                ["UMU_ID"] = "umu-legacy",
                ["GAMEID"] = "umu-legacy",
            },
            umuId: "umu-3513350");

        Assert.Equal("umu-3513350", plan.Environment["UMU_ID"]);
        Assert.Equal("umu-3513350", plan.Environment["GAMEID"]);
    }

    [Fact]
    public void BuildPlan_UmuIdAbsent_LegacyEnvUmuIdStillOverrides()
    {
        // 守卫的另一半语义：launch.umuId 未设置时，env 覆盖通道保持原样（存量手工配置）
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("BuildPlan 仅 Linux（守卫为纯字典逻辑，由 Linux 腿覆盖）");
        }

        var plan = BuildPlanWithExtraEnvironment(
            new Dictionary<string, string>
            {
                ["UMU_ID"] = "umu-legacy",
                ["GAMEID"] = "umu-legacy",
            },
            umuId: null);

        Assert.Equal("umu-legacy", plan.Environment["UMU_ID"]);
        Assert.Equal("umu-legacy", plan.Environment["GAMEID"]);
    }

    [Fact]
    public void BuildEntryCommand_GameArguments_AppendedAfterExe()
    {
        // 游戏自身命令行参数（自定义启动选项 %command% 之后的 token，2026-10-03；含鸣潮
        // -krqlv=<tier>）追加在 exe 之后——两条 return 形态（host runtime 直连 / 容器 entry）
        // 都要带上；null/空不追加。纯逻辑跨平台可测（BuildPlan 仅 Linux）。
        var dir = _temp.Path;
        var manifestDir = Path.Combine(dir, "manifest");
        Directory.CreateDirectory(manifestDir);
        File.WriteAllText(Path.Combine(manifestDir, "toolmanifest.vdf"), """
            "manifest"
            {
              "commandline" "/proton %verb%"
              "compatmanager_layer_name" "proton"
            }
            """);
        var manifest = ToolManifest.Load(manifestDir);
        var host = SteamRuntimeCatalog.Host;

        var argv = NativeUmuLauncher.BuildEntryCommand(
            manifest, host, "run", "/g/a.exe", gameArguments: ["-krqlv=uhd"]);

        Assert.Equal("-krqlv=uhd", argv[^1]);
        Assert.Equal("/g/a.exe", argv[^2]);

        var multi = NativeUmuLauncher.BuildEntryCommand(
            manifest, host, "run", "/g/a.exe", gameArguments: ["-dx11", "--lang=zh"]);
        Assert.Equal(new[] { "/g/a.exe", "-dx11", "--lang=zh" }, multi.TakeLast(3));

        var noArg = NativeUmuLauncher.BuildEntryCommand(
            manifest, host, "run", "/g/a.exe", gameArguments: null);
        Assert.Equal("/g/a.exe", noArg[^1]);
    }

    [Fact]
    public void BuildPlan_GameArguments_AppendedAfterExe_BeforeQualityArgument()
    {
        // 两条链同序（2026-10-03 Steam 语义）：exe → 用户参数（%command% 之后，含
        // {exe}/{installDir} 占位符展开）→ 启动器生成的 -krqlv 档位参数
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("BuildPlan 仅 Linux（纯逻辑由 BuildEntryCommand 跨平台用例覆盖）");
        }

        var plan = BuildPlanWithExtraEnvironment(
            new Dictionary<string, string>(), umuId: null,
            gameArguments: ["-dx11", "{installDir}/cfg"],
            resourceQualityTier: "hd");

        var args = plan.Arguments;
        Assert.True(
            args.IndexOf("Game.exe", StringComparison.Ordinal) < args.IndexOf("-dx11", StringComparison.Ordinal)
            && args.IndexOf("-dx11", StringComparison.Ordinal) < args.IndexOf("-krqlv=hd", StringComparison.Ordinal),
            $"参数顺序应为 exe → 用户参数 → -krqlv，实际：{args}");
        Assert.Contains(_temp.FilePath("game", "cfg").Replace('\\', '/'), args, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPlan_ManagedProtonVerb_NotOverridableByUserEnvironment()
    {
        // F70：PROTON_VERB 是启动器托管键（UmuEnvironment.Build 白名单校验/缺省回退）——
        // 用户 env 同名键不得反超，否则空串/非法动词产出残缺 --verb 命令
        //（与 PROTONPATH/UMU_ID 的托管守卫同纪律）
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("BuildPlan 仅 Linux（守卫为纯字典逻辑，由 Linux 腿覆盖）");
        }

        var plan = BuildPlanWithExtraEnvironment(
            new Dictionary<string, string>
            {
                ["PROTON_VERB"] = "runwiththeball",
            },
            umuId: null);

        Assert.Equal(UmuLaunchRequest.DefaultVerb, plan.Environment["PROTON_VERB"]);
    }

    /// <summary>BuildPlan 组装（守卫两腿共用）：假 Proton + steamrt4 运行时 + 真实 exe。</summary>
    private UmuNativeLaunchPlan BuildPlanWithExtraEnvironment(
        Dictionary<string, string> extraEnvironment, string? umuId, CapturingLogger? logger = null,
        IReadOnlyList<string>? gameArguments = null, string? resourceQualityTier = null)
    {
        var runner = new FakeProcessRunner();
        var launcher = new NativeUmuLauncher(runner, provisioner: null, logger: logger, dataHome: _temp.Path);
        var protonDir = CreateFakeProton();
        var runtimeDir = UmuPaths.RuntimeDirectory("steamrt4", _temp.Path);
        Directory.CreateDirectory(runtimeDir);
        File.WriteAllText(Path.Combine(runtimeDir, "_v2-entry-point"), "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(runtimeDir, UmuPaths.InstallMarkerName), "ok");

        var install = _temp.FilePath("game");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "Game.exe"), "x");

        var manifest = ToolManifest.Load(protonDir);
        return launcher.BuildPlan(
            "wuthering-waves", install, "Game.exe", protonDir, manifest,
            SteamRuntimeCatalog.Default,
            extraEnvironment: extraEnvironment,
            dataHomeOverride: _temp.Path,
            umuId: umuId,
            gameArguments: gameArguments,
            resourceQualityTier: resourceQualityTier);
    }

    private static IReadOnlyList<string> BuildSampleEntry()
    {
        var dir = Path.Combine(Path.GetTempPath(), "yagl-native-umu-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "toolmanifest.vdf"), """
                "manifest"
                {
                  "commandline" "/proton %verb%"
                  "compatmanager_layer_name" "proton"
                }
                """);
            var manifest = ToolManifest.Load(dir);
            return NativeUmuLauncher.BuildEntryCommand(
                manifest, SteamRuntimeCatalog.Default, "run", "/g/a.exe", dataHome: dir);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    private string CreateFakeProton()
    {
        var dir = _temp.FilePath("GE-Proton");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "toolmanifest.vdf"), """
            "manifest"
            {
              "commandline" "/proton %verb%"
              "compatmanager_layer_name" "proton"
            }
            """);
        File.WriteAllText(Path.Combine(dir, "proton"), "#!/bin/sh\n");
        return dir;
    }
}
