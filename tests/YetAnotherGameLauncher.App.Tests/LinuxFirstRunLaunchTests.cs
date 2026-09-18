using System.Text.Json;
using Xunit;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Utilities;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// Linux 首运启动模板兜底：默认模板的裸 {exe} 在 Linux 上无法运行 Windows 客户端
/// （Exec format error），物化配置后应立即升级为原生 umu（内置 C# 链）并落盘；
/// Windows 首运与用户自定义模板不受影响。平台与 Proton 清单经 VmFactory 注入，双平台确定性。
/// 审计修复（2026-09-19）：原实现对落盘 JSON 原文做 Contains/正则（D4：转义可骗过 + 锁死
/// 序列化格式）；现读盘后反序列化为 GameCatalog 断模型值（SettingsHeadlessTests.DeserializeConfig 同款）。
/// </summary>
[Collection("sequential")]
public class LinuxFirstRunLaunchTests
{
    /// <summary>从磁盘重新解析 games.json：文件级断言不走内存目录，也不做字符串 Contains。</summary>
    private static GameCatalog DeserializeConfig(string path) =>
        JsonSerializer.Deserialize<GameCatalog>(File.ReadAllText(path), Json.Default)
        ?? throw new InvalidOperationException("配置文件反序列化结果为空");

    [Fact]
    public async Task FirstRun_Linux_UpgradesBareDirectTemplateToNativeUmu()
    {
        using var ctx = VmFactory.Build(
            configJson: null,
            templateFactory: () => VmFactory.SampleConfigJson,
            platformInfo: new FakePlatformInfo(isLinux: true, nvidiaGpuPresent: true),
            linuxProtonVersions: ["GE-Proton10-9"]);

        await ctx.Vm.InitializeAsync();

        var saved = DeserializeConfig(ctx.ConfigPath);
        Assert.All(saved.Games, g =>
            Assert.Equal("native-umu {exe}", g.Launch.CommandTemplate));
        // 鸣潮反作弊伪装随推荐一并落盘 + NVIDIA 分支 + 默认 DW-Proton 发行版代号
        var wuwaEnv = saved.Games[0].Launch.Environment;
        Assert.Equal("1", wuwaEnv.GetValueOrDefault("SteamOS"));
        Assert.Equal("1", wuwaEnv.GetValueOrDefault("PROTON_ENABLE_NVAPI"));
        Assert.Equal("DW-Proton", wuwaEnv.GetValueOrDefault("PROTONPATH"));
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

        var saved = DeserializeConfig(ctx.ConfigPath);
        Assert.All(saved.Games, g =>
            Assert.Equal("native-umu {exe}", g.Launch.CommandTemplate));
        var wuwaLaunch = saved.Games[0].Launch;
        Assert.False(string.IsNullOrWhiteSpace(wuwaLaunch.Environment.GetValueOrDefault("GAMEID")));
        Assert.False(string.IsNullOrWhiteSpace(wuwaLaunch.Environment.GetValueOrDefault("WINEPREFIX")));
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

        var saved = DeserializeConfig(ctx.ConfigPath);
        Assert.All(saved.Games, g =>
            Assert.Equal("native-umu {exe}", g.Launch.CommandTemplate)); // 有 wine 也不退回 wine 直启（统一 umu 链）
        Assert.False(string.IsNullOrWhiteSpace(
            saved.Games[0].Launch.Environment.GetValueOrDefault("WINEPREFIX")));
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

        // 用户显式选择的自定义模板不被覆盖（无迁移 env 注入）
        var saved = DeserializeConfig(ctx.ConfigPath);
        Assert.Equal("steam -applaunch 2530", saved.Games[0].Launch.CommandTemplate);
        Assert.DoesNotContain(saved.Games[0].Launch.Environment, kv =>
            kv.Key.Contains("wine", StringComparison.OrdinalIgnoreCase) ||
            kv.Value.Contains("GE-Proton", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FirstRun_Windows_KeepsBareDirectTemplate()
    {
        // platformInfo 缺省 = Windows 假平台：首运保持 {exe} 原样（双平台确定性）
        using var ctx = VmFactory.Build(
            configJson: null,
            templateFactory: () => VmFactory.SampleConfigJson);

        await ctx.Vm.InitializeAsync();

        var saved = DeserializeConfig(ctx.ConfigPath);
        var wuwaLaunch = saved.Games[0].Launch;
        Assert.Equal("{exe}", wuwaLaunch.CommandTemplate);
        Assert.DoesNotContain(wuwaLaunch.Environment, kv =>
            kv.Key.StartsWith("STEAM_COMPAT", StringComparison.Ordinal) ||
            kv.Key.Contains("wine", StringComparison.OrdinalIgnoreCase));
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

        var saved = DeserializeConfig(ctx.ConfigPath);
        Assert.Equal("native-umu {exe}", saved.Games[0].Launch.CommandTemplate);
        Assert.Equal(5, saved.Settings.SchemaVersion);
    }

    [Fact]
    public async Task Migration_Linux_CustomTemplate_LeftUntouched_ButVersionBumped()
    {
        using var ctx = VmFactory.Build(
            configJson: CustomTemplateJson,
            platformInfo: new FakePlatformInfo(isLinux: true),
            linuxProtonVersions: ["GE-Proton10-9"]);

        await ctx.Vm.InitializeAsync();

        var saved = DeserializeConfig(ctx.ConfigPath);
        Assert.Equal("steam -applaunch 2530", saved.Games[0].Launch.CommandTemplate); // 自定义模板不动
        Assert.Equal(5, saved.Settings.SchemaVersion); // 但迁移标记落盘
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

        var saved = DeserializeConfig(ctx.ConfigPath);
        var launch = saved.Games[0].Launch;
        Assert.Equal("native-umu {exe}", launch.CommandTemplate);
        Assert.Equal(5, saved.Settings.SchemaVersion);
        Assert.Equal("umu-wuthering-waves", launch.Environment.GetValueOrDefault("GAMEID")); // 既有 umu 环境保留
        Assert.Equal("DW-Proton", launch.Environment.GetValueOrDefault("PROTONPATH")); // 缺省发行版代号落盘
        Assert.False(string.IsNullOrWhiteSpace(launch.Environment.GetValueOrDefault("STEAM_COMPAT_DATA_PATH")));
    }

    [Fact]
    public async Task Migration_Schema4_BareTemplate_AlsoUpgraded()
    {
        // 审计修正（2026-09-19）：原测试注释称"schema4 裸模板是既成事实不再触碰"，且用
        // Contains("{exe}") 断言——该弱断言对 "native-umu {exe}" 同样为真，掩盖了实际行为。
        // 生产语义（MigrateLinuxBareLaunchTemplatesAsync 注释）：schema 4 迁移升级的正是
        // "首运升级功能上线之前物化的裸 {exe}"；schema ≥ 5 才永不触碰。
        using var ctx = VmFactory.Build(
            configJson: MigratedConfigJson,
            platformInfo: new FakePlatformInfo(isLinux: true),
            linuxProtonVersions: ["GE-Proton10-9"]);

        await ctx.Vm.InitializeAsync();

        var saved = DeserializeConfig(ctx.ConfigPath);
        Assert.Equal("native-umu {exe}", saved.Games[0].Launch.CommandTemplate); // 裸模板被 schema4 迁移升级
        Assert.Equal(5, saved.Settings.SchemaVersion); // 版本推进到 5（此后永不触碰）
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

        var saved = DeserializeConfig(ctx.ConfigPath);
        Assert.Equal("umu-run {exe}", saved.Games[0].Launch.CommandTemplate);
        Assert.Equal(5, saved.Settings.SchemaVersion);
        Assert.False(saved.Games[0].Launch.Environment.ContainsKey("PROTONPATH"));
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

    /// <summary>schema 4 迁移前的存量配置（裸 {exe}；schema4 迁移会把它升级为推荐链——审计修正后的语义）。</summary>
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
