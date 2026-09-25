using Xunit;
using YetAnotherGameLauncher.TestSupport;
using YetAnotherGameLauncher.ViewModels;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 设置持久化失败分支回归（2026-09-20 三审）：代理/限速保存曾丢弃 TrySaveCatalogAsync 的
/// 布尔结果——games.json 写不进时消息槽照样弹"已保存"，重启后设置静默回退。
/// 通过把配置所在目录临时改为不可写构造持久化失败（Linux 权限位；root 下跳过）。
/// </summary>
[Collection("sequential")]
public class SettingsSaveFailureTests : IDisposable
{
    private readonly VmFactory.Context _ctx;

    public SettingsSaveFailureTests() => _ctx = VmFactory.Build();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public async Task SaveProxy_PersistFailure_ShowsFailureInsteadOfSuccess()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("directory permission bits are Unix-only; the Windows leg covers this via file-lock semantics elsewhere");
        }

        await _ctx.Vm.InitializeAsync();
        _ctx.Vm.ShowSettingsCommand.Execute(null);
        var settings = (SettingsViewModel)_ctx.Vm.CurrentPage!;
        settings.ProxyManual = true;
        settings.ProxyAddressDraft = "http://127.0.0.1:7890";

        MakeDirectoryUnwritable(_ctx.TempDir.Path);
        try
        {
            await settings.SaveProxyCommand.ExecuteAsync(null);

            Assert.True(settings.ProxySave.Failed, "持久化失败必须落到失败消息位，不得弹已保存");
        }
        finally
        {
            RestoreDirectoryWritable(_ctx.TempDir.Path);
        }
    }

    [Fact]
    public async Task SaveDownloadLimit_PersistFailure_ShowsFailureInsteadOfSuccess()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("directory permission bits are Unix-only");
        }

        await _ctx.Vm.InitializeAsync();
        _ctx.Vm.ShowSettingsCommand.Execute(null);
        var settings = (SettingsViewModel)_ctx.Vm.CurrentPage!;
        settings.SpeedLimitMbDraft = "12";

        MakeDirectoryUnwritable(_ctx.TempDir.Path);
        try
        {
            await settings.SaveDownloadLimitCommand.ExecuteAsync(null);

            Assert.True(settings.SpeedLimitSave.Failed);
        }
        finally
        {
            RestoreDirectoryWritable(_ctx.TempDir.Path);
        }
    }

    private static void MakeDirectoryUnwritable(string dir)
    {
        if (!DacExemptionProbe.TryMakeDirectoryUnwritable(dir))
        {
            Assert.Skip("非 root 才能通过权限位制造写失败（当前以 root 运行，权限注入无效）");
        }
    }

    private static void RestoreDirectoryWritable(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
