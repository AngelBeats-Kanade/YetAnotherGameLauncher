using Xunit;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 配置加载失败路径的回归：games.json 是用户手改的受支持工作流，任何失败分支都绝不写盘——
/// 曾有缺陷：校验失败分支把空目录设回 Catalog，初始化期的迁移 SaveAsync 直接覆盖用户文件；
/// 修复后该缺陷的同族路径（关窗回写窗口状态、失败态会话里的设置保存）也必须被
/// "Catalog is null 静默跳过"防线拦住，这里一并固化。
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
        // 空文件必然校验失败（空串走 invalid JSON 分支）；文件字节数在初始化前后必须一致
        var ctx = VmFactory.Build(configJson: "");
        var before = await File.ReadAllTextAsync(ctx.ConfigPath);

        await ctx.Vm.InitializeAsync();

        Assert.True(ctx.Vm.ConfigError);
        Assert.False(string.IsNullOrWhiteSpace(ctx.Vm.StatusMessage));
        Assert.Empty(ctx.Vm.Games);
        Assert.Equal(before, await File.ReadAllTextAsync(ctx.ConfigPath));
    }

    [Fact]
    public async Task InitializeAsync_InvalidConfig_PersistWindowState_NeverWritesFile()
    {
        // 看完错误提示随手关窗是最自然的用户动作：窗口状态回写（Catalog null 早退防线）
        // 绝不能把空目录 JSON 落盘覆盖用户文件——曾有修复只挡住了初始化期写入而漏过这条
        var ctx = VmFactory.Build(configJson: "");
        var before = await File.ReadAllTextAsync(ctx.ConfigPath);

        await ctx.Vm.InitializeAsync();
        Assert.True(ctx.Vm.ConfigError);

        ctx.Vm.PersistWindowState(1464, 720, maximized: false);

        Assert.Equal(before, await File.ReadAllTextAsync(ctx.ConfigPath));
    }

    [Fact]
    public async Task InitializeAsync_InvalidConfig_SidebarToggle_NeverWritesFile()
    {
        // 同上一条的防线（PersistSidebarExpandedAsync 与窗口状态共用 Catalog null 早退）：
        // 失败态会话里任何设置保存（侧栏开合/语言/代理/限速）都不得写盘。
        // 属性 setter 是 fire-and-forget：有界等待后断言"仍未写盘"
        var ctx = VmFactory.Build(configJson: "");
        var before = await File.ReadAllTextAsync(ctx.ConfigPath);

        await ctx.Vm.InitializeAsync();
        Assert.True(ctx.Vm.ConfigError);

        ctx.Vm.IsSidebarExpanded = false;
        await Task.Delay(400);

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
