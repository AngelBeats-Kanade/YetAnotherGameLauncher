using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Core.Services.Umu;
using YetAnotherGameLauncher.TestSupport;
using Xunit;

namespace YetAnotherGameLauncher.Core.Tests.Services.Umu;

public sealed class NativeUmuCoreTests : IDisposable
{
    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void VdfMiniParser_ParsesToolManifestFields()
    {
        const string vdf = """
            "manifest"
            {
              "version" "2"
              "commandline" "/proton %verb%"
              "require_tool_appid" "4183110"
              "compatmanager_layer_name" "proton"
            }
            """;
        var root = VdfMiniParser.Parse(vdf);
        Assert.Equal("2", VdfMiniParser.GetString(root, "manifest", "version"));
        Assert.Equal("/proton %verb%", VdfMiniParser.GetString(root, "manifest", "commandline"));
        Assert.Equal("4183110", VdfMiniParser.GetString(root, "manifest", "require_tool_appid"));
        Assert.Equal("proton", VdfMiniParser.GetString(root, "manifest", "compatmanager_layer_name"));
    }

    [Fact]
    public void SteamRuntimeCatalog_MapsAppIdAndArchiveName()
    {
        var sniper = SteamRuntimeCatalog.FromAppId("1628350");
        Assert.NotNull(sniper);
        Assert.Equal("sniper", sniper!.Name);
        Assert.Equal("steamrt3", sniper.Variant);
        Assert.Equal("SteamLinuxRuntime_sniper.tar.xz", SteamRuntimeCatalog.ArchiveFileName(sniper));

        var steamrt4 = SteamRuntimeCatalog.FromAppId("4183110");
        Assert.NotNull(steamrt4);
        Assert.Equal("SteamLinuxRuntime_4.tar.xz", SteamRuntimeCatalog.ArchiveFileName(steamrt4!));
        Assert.Equal("/steamrt4/images", SteamRuntimeCatalog.ImagesPathPrefix(steamrt4!));

        Assert.Null(SteamRuntimeCatalog.FromAppId("999"));
    }

    [Fact]
    public void ToolManifest_Load_BuildsEntryCommandWithVerb()
    {
        var dir = _temp.FilePath("GE-Proton9-27");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "toolmanifest.vdf"), """
            "manifest"
            {
              "commandline" "/proton %verb%"
              "compatmanager_layer_name" "proton"
              "require_tool_appid" "4183110"
            }
            """);
        File.WriteAllText(Path.Combine(dir, "compatibilitytool.vdf"), """
            "compatibilitytools"
            {
              "compat_tools"
              {
                "GE-Proton9-27"
                {
                  "display_name" "GE-Proton9-27"
                }
              }
            }
            """);
        File.WriteAllText(Path.Combine(dir, "proton"), "#!/usr/bin/env python3\n");

        var manifest = ToolManifest.Load(dir);
        Assert.True(manifest.IsProton);
        Assert.Equal("GE-Proton9-27", manifest.DisplayName);
        Assert.Equal("4183110", manifest.RequiredToolAppId);
        Assert.Equal("steamrt4", manifest.RequiredRuntime.Variant);

        var argv = manifest.BuildEntryCommand("waitforexitandrun");
        Assert.EndsWith("/proton", argv[0].Replace('\\', '/'));
        Assert.Equal("waitforexitandrun", argv[1]);
    }

    [Fact]
    public void UmuEnvironment_Build_SetsCompatKeysAndAppId()
    {
        var exe = Path.Combine(_temp.Path, "game", "Game.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "x");
        var proton = Path.Combine(_temp.Path, "proton");
        Directory.CreateDirectory(proton);
        var pfx = Path.Combine(_temp.Path, "pfx");

        var env = UmuEnvironment.Build(new UmuLaunchRequest(
            "wuthering-waves",
            exe,
            Path.GetDirectoryName(exe)!,
            proton,
            pfx,
            SteamRuntimeCatalog.Default));

        Assert.Equal("umu-wuthering-waves", env["GAMEID"]);
        Assert.Equal("umu-wuthering-waves", env["UMU_ID"]);
        Assert.Equal(Path.GetFullPath(pfx), env["WINEPREFIX"]);
        Assert.Equal(env["WINEPREFIX"], env["STEAM_COMPAT_DATA_PATH"]);
        Assert.Equal(Path.GetFullPath(proton), env["PROTONPATH"]);
        Assert.Equal("waitforexitandrun", env["PROTON_VERB"]);
        Assert.Equal("none", env["STORE"]);
        Assert.Equal(Path.GetFullPath(exe), env["EXE"]);
        Assert.False(string.IsNullOrEmpty(env["STEAM_COMPAT_APP_ID"]));
        Assert.Contains(Path.GetFullPath(proton), env["STEAM_COMPAT_TOOL_PATHS"]);
    }

    [Fact]
    public void UmuPrefix_Setup_CreatesLayout()
    {
        var pfx = _temp.FilePath("prefixes", "g1");
        UmuPrefix.Setup(pfx, unixUserName: "tester");

        Assert.True(Directory.Exists(pfx));
        Assert.True(Directory.Exists(Path.Combine(pfx, "shadercache")));
        Assert.True(Directory.Exists(Path.Combine(pfx, "gstreamer-1.0")));
        Assert.True(File.Exists(Path.Combine(pfx, "tracked_files")));
        // pfx 符号链接或退化目录均视为就位
        Assert.True(Directory.Exists(Path.Combine(pfx, "pfx")) || new DirectoryInfo(Path.Combine(pfx, "pfx")).LinkTarget is not null);
    }

    [Fact]
    public void NativeUmuLauncher_BuildEntryCommand_WrapsWithRuntimeEntryPoint()
    {
        var protonDir = _temp.FilePath("GE-Proton");
        Directory.CreateDirectory(protonDir);
        File.WriteAllText(Path.Combine(protonDir, "toolmanifest.vdf"), """
            "manifest"
            {
              "commandline" "/proton %verb%"
              "compatmanager_layer_name" "proton"
            }
            """);
        var manifest = ToolManifest.Load(protonDir);
        var runtimeDir = UmuPaths.RuntimeDirectory("steamrt4", _temp.Path);
        Directory.CreateDirectory(runtimeDir);
        File.WriteAllText(Path.Combine(runtimeDir, "_v2-entry-point"), "#!/bin/sh\n");

        var exe = "/games/Game.exe";
        var argv = NativeUmuLauncher.BuildEntryCommand(manifest, SteamRuntimeCatalog.Default, "run", exe);

        Assert.EndsWith("_v2-entry-point", argv[0].Replace('\\', '/'));
        Assert.Equal("--verb=run", argv[1]);
        Assert.Equal("--", argv[2]);
        Assert.EndsWith("/proton", argv[3].Replace('\\', '/'));
        Assert.Equal("run", argv[4]);
        Assert.Equal(exe, argv[5]);
    }

    [Fact]
    public void ResolveNativeProtonRequest_UsesEnvThenRecommended()
    {
        Assert.Equal(
            "/x/GE-Proton",
            CompatTools.ResolveNativeProtonRequest(
                new Dictionary<string, string> { ["PROTONPATH"] = "/x/GE-Proton" },
                ["GE-Proton10-9"]));
        Assert.Equal(
            "GE-Proton10-9",
            CompatTools.ResolveNativeProtonRequest(
                new Dictionary<string, string>(),
                ["GE-Proton10-9", "dw-proton"]));
        Assert.Equal(
            "UMU-Proton",
            CompatTools.ResolveNativeProtonRequest(null, []));
    }

    [Fact]
    public void BuildNativeUmuLaunch_UsesTokenTemplate()
    {
        var launch = CompatTools.BuildNativeUmuLaunch("wuthering-waves", dataHome: _temp.Path);
        Assert.Equal(LaunchMode.NativeUmu, launch.Mode);
        Assert.Equal("native-umu {exe}", launch.CommandTemplate);
        Assert.Equal("umu-wuthering-waves", launch.Environment["GAMEID"]);
    }

    [Fact]
    public void BuildRecommendedLaunch_PrefersNativeUmu()
    {
        var launch = CompatTools.BuildRecommendedLaunch(
            "endfield",
            protonVersions: ["GE-Proton9-27"],
            dataHome: _temp.Path,
            umuRunPath: "/x/umu-run",
            winePath: "/x/wine");
        Assert.Equal(LaunchMode.NativeUmu, launch.Mode);
        Assert.StartsWith("native-umu", launch.CommandTemplate, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRecommendedLaunch_NativeDisabled_FallsBackToExternalUmu()
    {
        var launch = CompatTools.BuildRecommendedLaunch(
            "endfield",
            protonVersions: [],
            dataHome: _temp.Path,
            umuRunPath: "/x/umu-run",
            winePath: null,
            preferNativeUmu: false);
        Assert.Equal(LaunchMode.Umu, launch.Mode);
        Assert.StartsWith("/x/umu-run", launch.CommandTemplate, StringComparison.Ordinal);
    }
}
