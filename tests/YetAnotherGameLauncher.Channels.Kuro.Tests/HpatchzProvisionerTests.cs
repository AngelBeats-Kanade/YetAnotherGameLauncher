using Xunit;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Channels.Kuro.Tests;

/// <summary>
/// hpatchz 自动供给器（2026-10-02）：鸣潮 krpdiff 只能被社区验证构建（ww-manager 打包的
/// hpatchz.exe）读取——开源 HDiffPatch 全线 109（真机实证链见 HpatchzProvisioner 类注释）。
/// Linux 供给形态 = wine + exe + 独立 WINEPREFIX；Windows = exe 原生直跑。
/// </summary>
public class HpatchzProvisionerTests : IDisposable
{
    private readonly TempDir _tempDir = new();
    private readonly FakeDownloader _downloader = new();

    public void Dispose() => _tempDir.Dispose();

    private HpatchzProvisioner CreateProvisioner(Func<string?>? wineLocator = null) =>
        new(_downloader, dataDirectory: _tempDir.Path, wineLocator: wineLocator);

    /// <summary>造一个"存在且可执行"的 wine 替身（供给器对定位器结果同样做真实存在校验）。</summary>
    private string StubWine()
    {
        var path = _tempDir.FilePath("wine-stub");
        File.WriteAllText(path, "#!/bin/sh\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private void ServeExe() =>
        _downloader.Responses[HpatchzProvisioner.DownloadUrl] = "MZ-fake-exe"u8.ToArray();

    [Fact]
    public async Task EnsureAvailable_Linux_DownloadsExeAndRunsThroughWine()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("wine/prefix semantics are Linux-only; the windows leg covers the native branch below");
        }

        ServeExe();

        var tool = await CreateProvisioner(wineLocator: () => StubWine()).EnsureAvailableAsync();

        // 供给形态：wine 为进程、exe 为前缀参数、独立 WINEPREFIX 指向供给目录内
        Assert.Equal(StubWine(), tool.FileName);
        Assert.Equal("\"" + Path.Combine(_tempDir.Path, "tools", "hpatchz", HpatchzProvisioner.Version, "hpatchz.exe") + "\"",
            tool.ArgumentPrefix);
        Assert.Equal(
            Path.Combine(_tempDir.Path, "tools", "hpatchz", HpatchzProvisioner.Version, "prefix"),
            tool.Environment!["WINEPREFIX"]);
        // exe 落位（下载器写盘，无解压步骤——资产即单文件 exe）
        Assert.True(File.Exists(Path.Combine(
            _tempDir.Path, "tools", "hpatchz", HpatchzProvisioner.Version, "hpatchz.exe")));
    }

    [Fact]
    public async Task EnsureAvailable_Windows_RunsExeNatively()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("native-exe branch is Windows-only; the linux leg covers the wine branch above");
        }

        ServeExe();

        var tool = await CreateProvisioner().EnsureAvailableAsync();

        Assert.EndsWith("hpatchz.exe", tool.FileName);
        Assert.Equal("", tool.ArgumentPrefix);
        Assert.Null(tool.Environment);
    }

    [Fact]
    public async Task EnsureAvailable_AlreadyReady_ZeroNetwork()
    {
        // 幂等：已就绪（exe 在）直接返回，不发任何网络请求
        ServeExe();
        var provisioner = CreateProvisioner(wineLocator: () => StubWine());
        await provisioner.EnsureAvailableAsync();

        _downloader.Requests.Clear();
        var again = await provisioner.EnsureAvailableAsync();

        Assert.Equal(StubWine(), again.FileName);
        Assert.Empty(_downloader.Requests);
    }

    [Fact]
    public async Task EnsureAvailable_Md5Mismatch_FoldsToUpdateException()
    {
        // 资产与嵌入 MD5 不符（release 被替换/CDN 损坏）按 UpdateException 折算——真下载器 +
        // 桩 HTTP 走校验链（FakeDownloader 不做 MD5 校验）
        var handler = new StubHttpHandler();
        handler.Map(HpatchzProvisioner.DownloadUrl, "tampered-or-corrupt");
        var provisioner = new HpatchzProvisioner(
            new HttpFileDownloader(new HttpClient(handler), new HttpFileDownloaderOptions { MaxAttempts = 1 }),
            dataDirectory: _tempDir.Path);

        var ex = await Assert.ThrowsAsync<UpdateException>(() => provisioner.EnsureAvailableAsync());

        Assert.Contains("hpatchz", ex.Message, StringComparison.Ordinal);
        Assert.Contains("MD5 mismatch", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureAvailable_LinuxWithoutWine_ActionableError()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("wine requirement is Linux-only");
        }

        ServeExe();
        // 定位器无结果且本机无系统/Proton wine（CI 与多数测试机）——可操作报错。
        // 本机装有 wine 的开发环境会让回退定位命中，先探测再决定 Skip（机器前提显式化）
        var hasLocalWine = CompatTools.FindSystemWine() is not null || CompatTools.FindProtonWine() is not null;
        if (hasLocalWine)
        {
            Assert.Skip("this machine has wine; the no-wine path cannot be constructed here");
        }

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => CreateProvisioner(wineLocator: () => null).EnsureAvailableAsync());

        Assert.Contains("wine", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToWinePath_MapsUnixRootToZDrive()
    {
        Assert.Equal("Z:/home/u/game/old dir", HpatchzProvisioner.ToWinePath("/home/u/game/old dir"));
    }
}
