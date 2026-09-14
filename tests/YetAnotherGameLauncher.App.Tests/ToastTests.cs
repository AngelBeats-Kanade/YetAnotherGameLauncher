using Xunit;

using YetAnotherGameLauncher.ViewModels;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 轻提示（toast）管线：ShowToast 容量上限（3 条，过载丢最旧）、DismissCommand 移除、
/// 种类透传；启动首轮预热不弹（状态胶囊承载），此后状态变化才弹（armed 门）。
/// 串行集合：与 headless 会话用户同队，避免并行调度踩中平台初始化竞态。
/// </summary>
[Collection("sequential")]
public class ToastTests : IDisposable
{
    private readonly VmFactory.Context _ctx;

    public ToastTests() => _ctx = VmFactory.Build();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task InitializeAsync_DoesNotToastOnFirstRefresh()
    {
        await _ctx.Vm.InitializeAsync();

        Assert.Empty(_ctx.Vm.Toasts); // 启动预热静默：状态胶囊本就承载
    }

    [Fact]
    public void ShowToast_CapsAtThree_DropsOldest()
    {
        _ctx.Vm.ShowToast("鸣潮", "第一条", ToastKind.Info);
        _ctx.Vm.ShowToast("鸣潮", "第二条", ToastKind.Success);
        _ctx.Vm.ShowToast("终末地", "第三条", ToastKind.Warning);
        _ctx.Vm.ShowToast("终末地", "第四条", ToastKind.Info);

        Assert.Equal(3, _ctx.Vm.Toasts.Count);
        Assert.Equal("第二条", _ctx.Vm.Toasts[0].Message); // 最旧的"第一条"被丢弃
        Assert.Equal("第四条", _ctx.Vm.Toasts[^1].Message);
        Assert.True(_ctx.Vm.Toasts[1].IsWarning);
        Assert.True(_ctx.Vm.Toasts[0].IsSuccess);
    }

    [Fact]
    public void DismissCommand_RemovesToast()
    {
        _ctx.Vm.ShowToast("鸣潮", "可预下载新版本", ToastKind.Warning);
        var toast = _ctx.Vm.Toasts[0];

        toast.DismissCommand.Execute(null);

        Assert.Empty(_ctx.Vm.Toasts);
    }

    [Fact]
    public async Task StatusChange_AfterFirstRefresh_RaisesToast()
    {
        // 首轮刷新（武装，不弹）→ 制造"检测到游戏文件"状态变化 → 二次刷新弹 Success toast
        await _ctx.Vm.InitializeAsync();
        var game = _ctx.Vm.Games[0];
        Assert.Empty(_ctx.Vm.Toasts);

        // 让配置路径上的 exe 出现，使状态从"未安装"翻转为"检测到游戏文件"（首轮时不存在）
        var exePath = Path.Combine(game.InstallDirPath, game.Game.Executable.Replace('\\', '/'));
        Directory.CreateDirectory(Path.GetDirectoryName(exePath)!);
        await File.WriteAllTextAsync(exePath, "x");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(exePath, UnixFileMode.UserExecute);
        }

        await game.RefreshAsync();

        var toast = Assert.Single(_ctx.Vm.Toasts);
        Assert.Equal(game.DisplayName, toast.Title);
        Assert.Equal(game.StatusText, toast.Message);
        Assert.Equal(ToastKind.Success, toast.Kind);

        // 回归：StatusText 在刷新早期被清空——重复刷新（状态语义未变）不得再弹
        await game.RefreshAsync();
        Assert.Single(_ctx.Vm.Toasts);
    }
}
