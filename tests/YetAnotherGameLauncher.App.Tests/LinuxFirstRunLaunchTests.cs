using System.Text.RegularExpressions;
using YetAnotherGameLauncher.TestSupport;
using YetAnotherGameLauncher.ViewModels;
using Xunit;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// Linux 首运启动模板兜底：默认模板的裸 {exe} 在 Linux 上无法运行 Windows 客户端
/// （Exec format error），物化配置后应立即升级为推荐 Proton（无可用版本则 wine）并落盘；
/// Windows 首运与用户自定义模板不受影响。平台与 Proton 清单经 VmFactory 注入，双平台确定性。
/// </summary>
[Collection("sequential")]
public class LinuxFirstRunLaunchTests
{
    [Fact]
    public async Task FirstRun_Linux_UpgradesBareDirectTemplateToRecommendedProton()
    {
        using var ctx = VmFactory.Build(
            configJson: null,
            templateFactory: () => VmFactory.SampleConfigJson,
            platformInfo: new FakePlatformInfo(isLinux: true, nvidiaGpuPresent: true),
            linuxProtonVersions: ["GE-Proton10-9"]);

        await ctx.Vm.InitializeAsync();

        var saved = await File.ReadAllTextAsync(ctx.ConfigPath);
        Assert.Contains("GE-Proton10-9", saved, StringComparison.Ordinal);
        Assert.Contains("SteamOS", saved, StringComparison.Ordinal); // 鸣潮反作弊伪装随推荐一并落盘
        Assert.Contains("PROTON_ENABLE_NVAPI", saved, StringComparison.Ordinal); // NVIDIA 分支
        // 两个游戏的裸 {exe} 模板都被升级为 Proton 启动
        Assert.Equal(2, Regex.Matches(saved, "run \\{exe\\}").Count);
    }

    [Fact]
    public async Task FirstRun_Linux_WithoutProton_FallsBackToWine()
    {
        using var ctx = VmFactory.Build(
            configJson: null,
            templateFactory: () => VmFactory.SampleConfigJson,
            platformInfo: new FakePlatformInfo(isLinux: true),
            linuxProtonVersions: []);

        await ctx.Vm.InitializeAsync();

        var saved = await File.ReadAllTextAsync(ctx.ConfigPath);
        Assert.Equal(2, Regex.Matches(saved, "wine \\{exe\\}").Count);
        Assert.DoesNotContain("STEAM_COMPAT", saved, StringComparison.Ordinal);
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
}
