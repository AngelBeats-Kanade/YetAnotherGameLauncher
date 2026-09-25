using Xunit;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Services.Umu;

namespace YetAnotherGameLauncher.Core.Tests.Services.Umu;

public class ToolManifestRuntimeTests
{
    [Fact]
    public void RequiredRuntime_UnknownAppId_ThrowsStructuredInsteadOfSilentHost()
    {
        // F23（artifacts/bugs.md）：toolmanifest 引用未收录的 runtime appid（Valve 新增 runtime
        // /社区 Proton 引用未收录变体，目录漂移）时 `FromAppId ?? Host` 把"未知"并入"无 appid"——
        // NativeUmuLauncher 按 host 跳过 runtime 安装，Proton 裸跑宿主环境静默降级（对照：已知
        // 缺失 runtime 有明确 UmuRuntimeMissing 报错）。修复后：appid 存在但未收录 → 结构化报错
        var manifest = new ToolManifest(
            "/tools/x", "/proton waitforexitandrun", "proton", RequiredToolAppId: "9999999", DisplayName: "X");

        var ex = Assert.Throws<LaunchException>(() => manifest.RequiredRuntime);

        Assert.Equal(LaunchFailureKind.UmuRuntimeMissing, ex.Kind);
    }

    [Fact]
    public void RequiredRuntime_NoAppId_IsHost()
    {
        // 无 appid = host 语义保持不变（F23 只收窄"未知 appid"形态）
        var manifest = new ToolManifest(
            "/tools/x", "/proton waitforexitandrun", "proton", RequiredToolAppId: null, DisplayName: "X");

        Assert.Equal("host", manifest.RequiredRuntime.Name);
    }
}
