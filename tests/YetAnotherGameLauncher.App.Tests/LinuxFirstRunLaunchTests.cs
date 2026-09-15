using System.Text.RegularExpressions;
using Xunit;
using YetAnotherGameLauncher.TestSupport;

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
        Assert.Contains("PROTONPATH", saved, StringComparison.Ordinal); // 默认 DW-Proton 发行版代号落盘
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

    // ---------- schemaVersion 4/5 迁移：存量配置的裸 {exe} 与外部 umu 模板一次性升级 ----------

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
        Assert.Contains("\"schemaVersion\": 5", saved, StringComparison.Ordinal);
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
        Assert.Contains("\"schemaVersion\": 5", saved, StringComparison.Ordinal); // 但迁移标记落盘
    }

    [Fact]
    public async Task Migration_Linux_LegacyUmuTemplate_UpgradedToNativeUmu()
    {
        // 存量外部 umu 模板（历史 BuildUmuLaunch 落盘的裸 "umu-run {exe}"，应用生成而非用户手写）：
        // 外部 umu-launcher 已移除，一次性升级为原生 umu 并补齐 STEAM_COMPAT_DATA_PATH / PROTONPATH
        using var ctx = VmFactory.Build(
            configJson: LegacyUmuConfigJson,
            platformInfo: new FakePlatformInfo(isLinux: true),
            linuxProtonVersions: ["GE-Proton10-9"]);

        await ctx.Vm.InitializeAsync();

        var saved = await File.ReadAllTextAsync(ctx.ConfigPath);
        Assert.Contains("native-umu {exe}", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("umu-run", saved, StringComparison.Ordinal);
        Assert.Contains("\"schemaVersion\": 5", saved, StringComparison.Ordinal);
        Assert.Contains("\"GAMEID\": \"umu-wuthering-waves\"", saved, StringComparison.Ordinal); // 既有 umu 环境保留
        Assert.Contains("\"PROTONPATH\": \"DW-Proton\"", saved, StringComparison.Ordinal); // 缺省发行版代号落盘
        Assert.Contains("STEAM_COMPAT_DATA_PATH", saved, StringComparison.Ordinal);
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

    [Fact]
    public async Task Migration_Schema5_NeverTouchesAgain()
    {
        // 已完成 v5 迁移的配置即使 umu-run 仍缺失也不再被改写（迁移只执行一次）
        using var ctx = VmFactory.Build(
            configJson: Schema5LegacyUmuConfigJson,
            platformInfo: new FakePlatformInfo(isLinux: true),
            linuxProtonVersions: ["GE-Proton10-9"]);

        await ctx.Vm.InitializeAsync();

        var saved = await File.ReadAllTextAsync(ctx.ConfigPath);
        Assert.Contains("umu-run {exe}", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("native-umu", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("PROTONPATH", saved, StringComparison.Ordinal);
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

    /// <summary>存量外部 umu 配置（schemaVersion 4，模板为历史 BuildUmuLaunch 落盘的裸 "umu-run {exe}"）。</summary>
    private const string LegacyUmuConfigJson = """
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
                "commandTemplate": "umu-run {exe}",
                "workingDirectory": "{installDir}",
                "environment": {
                  "GAMEID": "umu-wuthering-waves",
                  "UMU_ID": "umu-wuthering-waves",
                  "WINEPREFIX": "~/.local/share/yagl/prefixes/wuthering-waves"
                }
              }
            }
          ]
        }
        """;

    /// <summary>v5 迁移已完成的配置（外部 umu 模板保留 = 迁移后的既成事实，不再触碰）。</summary>
    private const string Schema5LegacyUmuConfigJson = """
        {
          "settings": { "installRoot": "~/yagl-test-games", "theme": "Dark", "maxParallelDownloads": 4, "schemaVersion": 5 },
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
                "commandTemplate": "umu-run {exe}",
                "workingDirectory": "{installDir}",
                "environment": {}
              }
            }
          ]
        }
        """;
}
