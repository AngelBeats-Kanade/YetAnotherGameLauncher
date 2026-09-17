using Xunit;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 配置加载失败路径的回归：games.json 是用户手改的受支持工作流，任何失败分支都绝不写盘——
/// 曾有缺陷：校验失败分支把空目录设回 Catalog 后无条件跑三次迁移（迁移推进 SchemaVersion
/// 并 SaveAsync），把用户文件覆盖成空配置，属于数据丢失。
/// </summary>
[Collection("sequential")]
public class ConfigFailureTests : IDisposable
{
    private readonly VmFactory.Context _ctx;

    public ConfigFailureTests() => _ctx = VmFactory.Build();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task InitializeAsync_InvalidConfig_NeverOverwritesUserFile()
    {
        // 空文件必然校验失败（"Config file is empty."）；文件字节数在初始化前后必须一致
        var ctx = VmFactory.Build(configJson: "");
        var before = await File.ReadAllTextAsync(ctx.ConfigPath);

        await ctx.Vm.InitializeAsync();

        Assert.True(ctx.Vm.ConfigError);
        Assert.False(string.IsNullOrWhiteSpace(ctx.Vm.StatusMessage));
        Assert.Empty(ctx.Vm.Games);
        Assert.Equal(before, await File.ReadAllTextAsync(ctx.ConfigPath));
    }

    [Fact]
    public async Task InitializeAsync_ConfigPathUnreadable_ShowsErrorWithoutCrash()
    {
        // configJson=null 不创建文件，再把该路径占位成目录：读取/首运生成都必然 IO 失败
        var ctx = VmFactory.Build(configJson: null);
        Directory.CreateDirectory(ctx.ConfigPath);

        await ctx.Vm.InitializeAsync();

        Assert.True(ctx.Vm.ConfigError);
        Assert.False(string.IsNullOrWhiteSpace(ctx.Vm.StatusMessage));
        Assert.Empty(ctx.Vm.Games);
    }
}
