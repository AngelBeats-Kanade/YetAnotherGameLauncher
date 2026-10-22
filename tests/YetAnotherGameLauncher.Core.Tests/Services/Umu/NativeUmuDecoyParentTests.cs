using Xunit;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Services.Umu;
using YetAnotherGameLauncher.Core.Utilities;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Core.Tests.Services.Umu;

/// <summary>
/// 原生 umu 链 × 诱饵父进程桥（2026-10-18）：开启后启动目标换成桥 helper、游戏命令行经
/// YAGL_BRIDGE_* 环境变量传递；候选缺失或开关关闭时回退直启。真机验证见 artifacts/bugs.md。
/// </summary>
public sealed class NativeUmuDecoyParentTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly FakeProcessRunner _runner = new();

    /// <summary>假 helper：本次启动的桥目录里有状态占位文件时写指定状态。</summary>
    private void WriteBridgeStatus(string content)
    {
        var bridgeRoot = Path.Combine(_temp.Path, "yagl", "bridge");
        if (!Directory.Exists(bridgeRoot))
        {
            return;
        }

        foreach (var status in Directory.EnumerateFiles(
                     bridgeRoot, "status-*.txt", SearchOption.AllDirectories))
        {
            File.WriteAllText(status, content);
        }
    }

    public NativeUmuDecoyParentTests()
    {
        _runner.Handler = _ =>
        {
            WriteBridgeStatus(DecoyParentBridge.ReadyStatus);
            return new ProcessResult(0, "", "");
        };
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task LaunchAsync_DecoyParent_TargetsBridgeHelperAndInjectsBridgeEnvironment()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("原生 umu 链仅 Linux（由 Linux 腿覆盖）");
        }

        var (protonDir, install) = SetupFixture(withPlatformProcess: true);
        var launcher = new NativeUmuLauncher(_runner, provisioner: null, dataHome: _temp.Path);

        await launcher.LaunchAsync(
            "arknights-endfield", install, "Endfield.exe", protonDir, decoyParent: true);

        var spec = Assert.Single(_runner.Specs);
        var exe = Path.GetFullPath(Path.Combine(install, "Endfield.exe"));
        var decoy = Path.Combine(_temp.Path, "yagl", "bridge", "arknights-endfield", "Games.exe");

        // 容器链的最终目标是桥 helper（挂起诱饵与游戏都由它在 wine 侧编排）；游戏 exe 不再进 argv
        Assert.Contains("yagl-parent-bridge.exe", spec.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain(exe, spec.Arguments, StringComparison.Ordinal);

        var env = spec.Environment!;
        Assert.Equal(DecoyParentBridge.ToWindowsPath(decoy), env[DecoyParentBridge.DecoyVariable]);
        Assert.Equal(DecoyParentBridge.ToWindowsPath(exe), env[DecoyParentBridge.TargetVariable]);
        Assert.Equal($"\"{DecoyParentBridge.ToWindowsPath(exe)}\"", env[DecoyParentBridge.CommandLineVariable]);
        Assert.Equal(
            DecoyParentBridge.ToWindowsPath(Path.GetFullPath(install)),
            env[DecoyParentBridge.WorkingDirectoryVariable]);
        // 状态通道同样是 Windows 路径（helper 在 wine 侧写、launcher 在主机侧读）
        Assert.StartsWith(
            @"Z:\", env[DecoyParentBridge.StatusVariable], StringComparison.Ordinal);
        // 诱饵已按候选内容落盘（校验只认文件名+签名+指纹，副本内容即候选本体）
        Assert.Equal("signed-decoy", File.ReadAllText(decoy));
    }

    [Fact]
    public async Task LaunchAsync_DecoyParent_GameArgumentsRideInBridgeCommandLine()
    {
        // 直启链里游戏参数是容器 argv 的独立元素；桥模式下改由 YAGL_BRIDGE_CMD 承载——
        // 含空格参数必须按命令行规则加引号，且不得再出现在容器 argv 里
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("原生 umu 链仅 Linux");
        }

        var (protonDir, install) = SetupFixture(withPlatformProcess: true);
        var launcher = new NativeUmuLauncher(_runner, provisioner: null, dataHome: _temp.Path);
        var exe = Path.GetFullPath(Path.Combine(install, "Endfield.exe"));

        await launcher.LaunchAsync(
            "arknights-endfield", install, "Endfield.exe", protonDir,
            decoyParent: true, gameArguments: ["-dx11", "cfg path"]);

        var spec = Assert.Single(_runner.Specs);
        Assert.Equal(
            $"\"{DecoyParentBridge.ToWindowsPath(exe)}\" -dx11 \"cfg path\"",
            spec.Environment![DecoyParentBridge.CommandLineVariable]);
        Assert.DoesNotContain("-dx11", spec.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaunchAsync_DecoyParent_HelperError_ThrowsActionableError()
    {
        // Linux 原生链同型（2026-10-20 复审）：helper 在创建游戏前失败时写 ERROR 状态，
        // 必须抛可操作错误，不能因 WaitForExit=false 显示「已启动」。
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("原生 umu 链仅 Linux");
        }

        var (protonDir, install) = SetupFixture(withPlatformProcess: true);
        var launcher = new NativeUmuLauncher(_runner, provisioner: null, dataHome: _temp.Path);
        _runner.Handler = _ =>
        {
            WriteBridgeStatus($"{DecoyParentBridge.ErrorStatusPrefix}4:game create failed");
            return new ProcessResult(0, "", "helper failed");
        };

        var ex = await Assert.ThrowsAsync<LaunchException>(
            () => launcher.LaunchAsync(
                "arknights-endfield", install, "Endfield.exe", protonDir, decoyParent: true));

        Assert.Equal(LaunchFailureKind.StartFailed, ex.Kind);
        Assert.Contains("退出码 4", ex.Message, StringComparison.Ordinal);
        Assert.Contains("game create failed", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(ex.LogPath);
    }

    [Fact]
    public async Task LaunchAsync_DecoyParent_StatusTimeout_ThrowsActionableError()
    {
        // 状态通道未就绪（helper 卡住/崩溃未写状态）→ 超时报错，不静默报「已启动」
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("原生 umu 链仅 Linux");
        }

        var (protonDir, install) = SetupFixture(withPlatformProcess: true);
        var appLogDirectory = _temp.FilePath("app-logs");
        var launcher = new NativeUmuLauncher(
            _runner,
            provisioner: null,
            dataHome: _temp.Path,
            logDirectory: _temp.FilePath("launch-logs"),
            appLogDirectory: appLogDirectory)
        {
            BridgeStatusTimeout = TimeSpan.FromMilliseconds(100),
            BridgeStartupTimeout = TimeSpan.FromMilliseconds(100),
        };
        _runner.Handler = _ => new ProcessResult(0, "", "");

        var ex = await Assert.ThrowsAsync<LaunchException>(
            () => launcher.LaunchAsync(
                "arknights-endfield", install, "Endfield.exe", protonDir, decoyParent: true));

        Assert.Equal(LaunchFailureKind.StartFailed, ex.Kind);
        Assert.Contains("未启动", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(ex.LogPath);
        var appLogPath = FileUtilities.ApplicationLogFilePath(
            appLogDirectory, DateOnly.FromDateTime(DateTime.Now));
        Assert.Contains(appLogPath, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaunchAsync_DecoyParent_HelperStartedButGameNotCreated_ReportsReadyTimeout()
    {
        // helper 已写出 STARTING，但游戏进程在 READY 窗口内没创建（helper 卡在创建阶段或崩溃，
        // 不再写 READY）→ Linux 链提示必须指向“游戏进程未创建”，不能误报“桥未启动”。
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("原生 umu 链仅 Linux");
        }

        var (protonDir, install) = SetupFixture(withPlatformProcess: true);
        var launcher = new NativeUmuLauncher(_runner, provisioner: null, dataHome: _temp.Path)
        {
            BridgeStatusTimeout = TimeSpan.FromMilliseconds(100),
            BridgeStartupTimeout = TimeSpan.FromSeconds(5),
        };
        _runner.Handler = _ =>
        {
            WriteBridgeStatus("STARTING");
            return new ProcessResult(0, "", "");
        };

        var ex = await Assert.ThrowsAsync<LaunchException>(
            () => launcher.LaunchAsync(
                "arknights-endfield", install, "Endfield.exe", protonDir, decoyParent: true));

        Assert.Equal(LaunchFailureKind.StartFailed, ex.Kind);
        Assert.Contains("已启动", ex.Message, StringComparison.Ordinal);
        Assert.Contains("未创建", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(ex.LogPath);
    }

    [Fact]
    public async Task LaunchAsync_DecoyParent_HelperStartsAfterReadyTimeout_StillSucceeds()
    {
        // 两段计时（2026-10-21）：READY 上限只在 helper 写出首个状态后生效；容器冷启动
        // 慢于 READY 上限但仍在启动窗口内时，不能提前判桥故障。
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("原生 umu 链仅 Linux");
        }

        var (protonDir, install) = SetupFixture(withPlatformProcess: true);
        var launcher = new NativeUmuLauncher(_runner, provisioner: null, dataHome: _temp.Path)
        {
            BridgeStatusTimeout = TimeSpan.FromMilliseconds(500),
            BridgeStartupTimeout = TimeSpan.FromSeconds(5),
        };
        Task? statusWriter = null;
        _runner.Handler = _spec =>
        {
            statusWriter = Task.Run(async () =>
            {
                await Task.Delay(1000);
                WriteBridgeStatus("STARTING");
                await Task.Delay(100);
                WriteBridgeStatus(DecoyParentBridge.ReadyStatus);
            });
            return new ProcessResult(0, "", "");
        };

        try
        {
            var result = await launcher.LaunchAsync(
                "arknights-endfield", install, "Endfield.exe", protonDir, decoyParent: true);

            Assert.Equal(0, result.ExitCode);
        }
        finally
        {
            // 观察后台写入异常：否则 WriteBridgeStatus 失败会被启动超时掩盖，看不到真实原因。
            if (statusWriter is not null)
            {
                await statusWriter;
            }
        }
    }

    [Fact]
    public async Task LaunchAsync_DecoyParent_BuildPlanFailure_CleansStatusFile()
    {
        // BuildPlan 在 helper 启动前失败（游戏 exe 缺失）时，刚创建的 status-*.txt 占位文件
        // 必须清理，不能在桥目录里积累
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("原生 umu 链仅 Linux");
        }

        var (protonDir, install) = SetupFixture(withPlatformProcess: true);
        File.Delete(Path.Combine(install, "Endfield.exe"));
        var launcher = new NativeUmuLauncher(_runner, provisioner: null, dataHome: _temp.Path);

        await Assert.ThrowsAsync<LaunchException>(
            () => launcher.LaunchAsync(
                "arknights-endfield", install, "Endfield.exe", protonDir, decoyParent: true));

        var bridgeRoot = Path.Combine(_temp.Path, "yagl", "bridge", "arknights-endfield");
        Assert.Empty(Directory.EnumerateFiles(bridgeRoot, "status-*.txt"));
        Assert.Empty(_runner.Specs);
    }

    [Fact]
    public async Task LaunchAsync_DecoyParent_NoCandidates_FallsBackToDirectLaunch()
    {
        // 候选全缺（连 Endfield.exe 都不在：夹具用非候选名的 Game.exe 构造）→ 回退直启
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("原生 umu 链仅 Linux");
        }

        var (protonDir, install) = SetupFixture(withPlatformProcess: false, executable: "Game.exe");
        var launcher = new NativeUmuLauncher(_runner, provisioner: null, dataHome: _temp.Path);
        var exe = Path.GetFullPath(Path.Combine(install, "Game.exe"));

        await launcher.LaunchAsync(
            "arknights-endfield", install, "Game.exe", protonDir, decoyParent: true);

        var spec = Assert.Single(_runner.Specs);
        // 回退直启：目标仍是游戏 exe、无桥环境变量、不产生桥目录
        Assert.Contains(exe, spec.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("yagl-parent-bridge.exe", spec.Arguments, StringComparison.Ordinal);
        Assert.False(spec.Environment!.ContainsKey(DecoyParentBridge.DecoyVariable));
        Assert.False(Directory.Exists(Path.Combine(install, ".yagl")));
    }

    [Fact]
    public async Task LaunchAsync_DecoyParentDisabled_UsesDirectTarget()
    {
        // 开关缺省关闭：即使候选齐全也不建桥（旗标是显式配置，不做隐式启用）
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("原生 umu 链仅 Linux");
        }

        var (protonDir, install) = SetupFixture(withPlatformProcess: true);
        var launcher = new NativeUmuLauncher(_runner, provisioner: null, dataHome: _temp.Path);
        var exe = Path.GetFullPath(Path.Combine(install, "Endfield.exe"));

        await launcher.LaunchAsync("arknights-endfield", install, "Endfield.exe", protonDir);

        var spec = Assert.Single(_runner.Specs);
        Assert.Contains(exe, spec.Arguments, StringComparison.Ordinal);
        Assert.False(spec.Environment!.ContainsKey(DecoyParentBridge.DecoyVariable));
        Assert.False(Directory.Exists(Path.Combine(install, ".yagl")));
    }

    /// <summary>假 Proton（steamrt4 runtime）+ 安装目录；executable = 游戏主程序名（默认 Endfield.exe——
    /// 它自身也是桥的兜底候选，需按用例语义选择是否提供）。</summary>
    private (string ProtonDir, string InstallDir) SetupFixture(
        bool withPlatformProcess, string executable = "Endfield.exe")
    {
        var protonDir = _temp.FilePath("GE-Proton");
        Directory.CreateDirectory(protonDir);
        File.WriteAllText(Path.Combine(protonDir, "toolmanifest.vdf"), """
            "manifest"
            {
              "commandline" "/proton %verb%"
              "compatmanager_layer_name" "proton"
              "require_tool_appid" "4183110"
            }
            """);
        File.WriteAllText(Path.Combine(protonDir, "proton"), "#!/bin/sh\n");

        var runtimeDir = UmuPaths.RuntimeDirectory("steamrt4", _temp.Path);
        Directory.CreateDirectory(runtimeDir);
        File.WriteAllText(Path.Combine(runtimeDir, "_v2-entry-point"), "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(runtimeDir, UmuPaths.InstallMarkerName), "ok");

        // 含空格目录名：与真实安装路径（"Arknights Endfield"）同形，覆盖命令行引号路径
        var install = _temp.FilePath("game", "Arknights Endfield");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, executable), "x");
        if (withPlatformProcess)
        {
            File.WriteAllText(Path.Combine(install, "PlatformProcess.exe"), "signed-decoy");
        }

        return (protonDir, install);
    }
}
