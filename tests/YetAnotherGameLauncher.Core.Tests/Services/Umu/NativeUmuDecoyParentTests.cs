using Xunit;
using YetAnotherGameLauncher.Core.Services.Umu;
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
        var decoy = Path.Combine(install, ".yagl", "bridge", "Games.exe");

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
