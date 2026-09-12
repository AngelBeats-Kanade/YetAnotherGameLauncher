using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Services.Umu;
using YetAnotherGameLauncher.TestSupport;
using Xunit;

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
        var runtimeDir = Path.Combine(UmuPaths.RuntimeDirectory("steamrt4", _temp.Path));
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
            extraEnvironment: new Dictionary<string, string> { ["CUSTOM"] = "{installDir}/data" },
            dataHomeOverride: _temp.Path);

        Assert.EndsWith("_v2-entry-point", plan.FileName.Replace('\\', '/'));
        Assert.Contains("--verb=waitforexitandrun", plan.Arguments, StringComparison.Ordinal);
        Assert.Contains(exe, plan.Arguments, StringComparison.Ordinal);
        Assert.Equal(install, plan.WorkingDirectory);
        Assert.Equal("umu-wuthering-waves", plan.Environment["GAMEID"]);
        Assert.Equal(Path.Combine(install, "data"), plan.Environment["CUSTOM"]);
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
