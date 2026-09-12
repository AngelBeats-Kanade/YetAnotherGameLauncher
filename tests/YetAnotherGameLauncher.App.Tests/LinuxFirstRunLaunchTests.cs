using System.Text.RegularExpressions;
using YetAnotherGameLauncher.TestSupport;
using YetAnotherGameLauncher.ViewModels;
using Xunit;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// Linux 首运启动模板兜底：默认模板的裸 {exe} 在 Linux 上无法运行 Windows 客户端
/// （Exec format error），物化配置后应立即升级为原生 umu（内置 C# 链）并落盘；
/// Windows 首运与用户自定义模板不受影响。平台与 Proton 清单经 VmFactory 注入，双平台确定性。
/// </summary>
[Collection("sequential")]
public class LinuxFirstRunLaunchTests
{
    [Fact]
    public async Task FirstRun_Linux_UpgradesBareDirectTemplateToNativeUmu()
    {
        using var ctx = VmFactory.Build(
            configJson: null,
            templateFactory: () => VmFactory.SampleConfigJson,
            platformInfo: new FakePlatformInfo(isLinux: true, nvidiaGpuPresent: true),
            linuxProtonVersions: ["GE-Proton10-9"]);

        await ctx.Vm.InitializeAsync();

        var saved = await File.ReadAllTextAsync(ctx.ConfigPath);
        Assert.Contains("native-umu", saved, StringComparison.Ordinal);
        Assert.Contains("SteamOS", saved, StringComparison.Ordinal); // 鸣潮反作弊伪装随推荐一并落盘
        Assert.Contains("PROTON_ENABLE_NVAPI", saved, StringComparison.Ordinal); // NVIDIA 分支
        Assert.Equal(2, Regex.Matches(saved, "native-umu \\{exe\\}").Count);
    }

    [Fact]
    public async Task FirstRun_Linux_WithoutProtonOrUmu_FallsBackToNativeUmuTemplate()
    {
        // 什么运行时都没装：仍给原生 umu 模板——启动时组件准备器自动下载
        using var ctx = VmFactory.Build(
            configJson: null,
            templateFactory: () => VmFactory.SampleConfigJson,
            platformInfo: new FakePlatformInfo(isLinux: true),
            linuxProtonVersions: []);

        await ctx.Vm.InitializeAsync();

        var saved = await File.ReadAllTextAsync(ctx.ConfigPath);
        Assert.Equal(2, Regex.Matches(saved, "native-umu \\{exe\\}").Count);
        Assert.Contains("GAMEID", saved, StringComparison.Ordinal);
        Assert.Contains("WINEPREFIX", saved, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FirstRun_Linux_WithUmuInstalled_StillUsesNativeUmuDefault()
    {
        using var ctx = VmFactory.Build(
            configJson: null,
            templateFactory: () => VmFactory.SampleConfigJson,
            platformInfo: new FakePlatformInfo(isLinux: true, nvidiaGpuPresent: true),
            linuxProtonVersions: ["GE-Proton10-9"],
            linuxUmuPath: "/home/u/.local/bin/umu-run");

        await ctx.Vm.InitializeAsync();

        var saved = await File.ReadAllTextAsync(ctx.ConfigPath);
        Assert.Equal(2, Regex.Matches(saved, "native-umu \\{exe\\}").Count);
        Assert.Contains("PROTON_ENABLE_NVAPI", saved, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FirstRun_Linux_WithoutUmuAndProton_ButWithWine_StillUsesNativeUmuDefault()
    {
        using var ctx = VmFactory.Build(
            configJson: null,
            templateFactory: () => VmFactory.SampleConfigJson,
            platformInfo: new FakePlatformInfo(isLinux: true),
            linuxProtonVersions: [],
            linuxWinePath: "/usr/bin/wine");

        await ctx.Vm.InitializeAsync();

        var saved = await File.ReadAllTextAsync(ctx.ConfigPath);
        Assert.Equal(2, Regex.Matches(saved, "native-umu \\{exe\\}").Count);
        Assert.Contains("WINEPREFIX", saved, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FirstRun_Linux_CustomTemplate_LeftUntouched()
    {
        using var ctx = VmFactory.Build(
            configJson: null,
            templateFactory: () => CustomTemplateJson,
            platformInfo: new FakePlatformInfo(isLinux: true),
            linuxProtonVersions: ["GE-Proton10-9"]);

        await ctx.Vm.InitializeAsync();

        // 用户显式选择的自定义模板不被覆盖
        var saved = await File.ReadAllTextAsync(ctx.ConfigPath);
        Assert.Contains("steam -applaunch 2530", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("wine", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("GE-Proton", saved, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FirstRun_Windows_KeepsBareDirectTemplate()
    {
        // platformInfo 缺省 = Windows 假平台：首运保持 {exe} 原样（双平台确定性）
        using var ctx = VmFactory.Build(
            configJson: null,
            templateFactory: () => VmFactory.SampleConfigJson);

        await ctx.Vm.InitializeAsync();

        var saved = await File.ReadAllTextAsync(ctx.ConfigPath);
        Assert.Contains("{exe}", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("wine", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("STEAM_COMPAT", saved, StringComparison.Ordinal);
    }

    // ---------- schemaVersion 4 迁移：存量配置的裸 {exe} 一次性升级 ----------

    [Fact]
    public async Task Migration_Linux_OldBareTemplate_UpgradedToRecommendedChain()
    {
        // 存量配置（首运升级功能上线前物化）仍是裸 {exe}：Linux 上一次性升级，游戏才能真正启动
        using var ctx = VmFactory.Build(
            configJson: OldConfigJson,
            platformInfo: new FakePlatformInfo(isLinux: true),
            linuxProtonVersions: ["GE-Proton10-9"]);

        await ctx.Vm.InitializeAsync();

        var saved = await File.ReadAllTextAsync(ctx.ConfigPath);
        Assert.Contains("native-umu", saved, StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\": 4", saved, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Migration_Linux_CustomTemplate_LeftUntouched_ButVersionBumped()
    {
        using var ctx = VmFactory.Build(
            configJson: CustomTemplateJson,
            platformInfo: new FakePlatformInfo(isLinux: true),
            linuxProtonVersions: ["GE-Proton10-9"]);

        await ctx.Vm.InitializeAsync();

        var saved = await File.ReadAllTextAsync(ctx.ConfigPath);
        Assert.Contains("steam -applaunch 2530", saved, StringComparison.Ordinal); // 自定义模板不动
        Assert.DoesNotContain("GE-Proton", saved, StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\": 4", saved, StringComparison.Ordinal); // 但迁移标记落盘
    }

    [Fact]
    public async Task Migration_Schema4_NeverTouchesAgain()
    {
        using var ctx = VmFactory.Build(
            configJson: MigratedConfigJson,
            platformInfo: new FakePlatformInfo(isLinux: true),
            linuxProtonVersions: ["GE-Proton10-9"]);

        await ctx.Vm.InitializeAsync();

        var saved = await File.ReadAllTextAsync(ctx.ConfigPath);
        Assert.DoesNotContain("GE-Proton", saved, StringComparison.Ordinal);
        Assert.Contains("{exe}", saved, StringComparison.Ordinal); // 裸模板保持原样
    }

    /// <summary>与 SampleConfigJson 同构，但鸣潮条目带用户自定义启动模板。</summary>
    private const string CustomTemplateJson = """
        {
          "settings": { "installRoot": "~/yagl-test-games", "theme": "Dark", "maxParallelDownloads": 4 },
          "games": [
            {
              "id": "wuthering-waves",
              "displayName": "鸣潮",
              "nameLocalized": { "zh-CN": "鸣潮", "en-US": "Wuthering Waves" },
              "channel": "kuro",
              "installDir": "WutheringWaves",
              "executable": "Client/Binaries/Win64/Client-Win64-Shipping.exe",
              "servers": [ { "id": "cn", "name": "国服" } ],
              "launch": {
                "commandTemplate": "steam -applaunch 2530",
                "workingDirectory": "{installDir}",
                "environment": {}
              }
            }
          ]
        }
        """;

    /// <summary>存量旧版配置形态（无 schemaVersion 或 schemaVersion &lt; 4，模板裸 {exe}）。</summary>
    private const string OldConfigJson = """
        {
          "settings": { "installRoot": "~/yagl-test-games", "theme": "Dark", "maxParallelDownloads": 4 },
          "games": [
            {
              "id": "wuthering-waves",
              "displayName": "鸣潮",
              "nameLocalized": { "zh-CN": "鸣潮", "en-US": "Wuthering Waves" },
              "channel": "kuro",
              "installDir": "WutheringWaves",
              "executable": "Client/Binaries/Win64/Client-Win64-Shipping.exe",
              "servers": [ { "id": "cn", "name": "国服" } ],
              "launch": {
                "commandTemplate": "{exe}",
                "workingDirectory": "{installDir}",
                "environment": {}
              }
            }
          ]
        }
        """;

    /// <summary>已完成 schema 4 迁移的配置（裸 {exe} 保留 = 用户/迁移后的既成事实，不再触碰）。</summary>
    private const string MigratedConfigJson = """
        {
          "settings": { "installRoot": "~/yagl-test-games", "theme": "Dark", "maxParallelDownloads": 4, "schemaVersion": 4 },
          "games": [
            {
              "id": "wuthering-waves",
              "displayName": "鸣潮",
              "nameLocalized": { "zh-CN": "鸣潮", "en-US": "Wuthering Waves" },
              "channel": "kuro",
              "installDir": "WutheringWaves",
              "executable": "Client/Binaries/Win64/Client-Win64-Shipping.exe",
              "servers": [ { "id": "cn", "name": "国服" } ],
              "launch": {
                "commandTemplate": "{exe}",
                "workingDirectory": "{installDir}",
                "environment": {}
              }
            }
          ]
        }
        """;
}
