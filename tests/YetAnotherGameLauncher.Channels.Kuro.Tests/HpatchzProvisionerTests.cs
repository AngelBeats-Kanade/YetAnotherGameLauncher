using Xunit;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Core.Utilities;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Channels.Kuro.Tests;

/// <summary>
/// hpatchz 自动供给器（2026-10-02）：PATH 无自备二进制时从官方 release 下载固定版本 v5.1.3
/// 解压落位。下载/解压路径的 chmod 断言为 Linux 语义（Windows 腿由 SelectAsset 直测覆盖
/// windows-x64 分支）。
/// </summary>
public class HpatchzProvisionerTests : IDisposable
{
    private readonly TempDir _tempDir = new();
    private readonly FakeDownloader _downloader = new();

    public void Dispose() => _tempDir.Dispose();

    private HpatchzProvisioner CreateProvisioner() => new(_downloader, dataDirectory: _tempDir.Path);

    [Fact]
    public async Task EnsureAvailable_DownloadsExtractsAndSetsExecutableBit()
    {
        // 下载 → 防御解压 → 定位 zip 内 hpatchz（顶层目录名不定的真实 release 形态）→
        // 显式补执行位（.NET ZipFile 不恢复 zip 内的 Unix 权限位）
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("chmod/executable-bit assertions are Unix semantics; windows-x64 asset selection covered by SelectAsset test");
        }

        var zip = TestZip.Create(("some-release-dir/hpatchz", "fake-elf"u8.ToArray()));
        _downloader.Responses[HpatchzProvisioner.BuildDownloadUrl("linux64.zip")] = zip;

        var toolPath = await CreateProvisioner().EnsureAvailableAsync();

        Assert.True(FileUtilities.IsExecutableFile(toolPath));
        Assert.Equal("fake-elf", await File.ReadAllTextAsync(toolPath));
        Assert.StartsWith(Path.Combine(_tempDir.Path, "tools", "hpatchz"), toolPath); // 布局：数据目录下 tools/hpatchz/v{版本}
        // 临时产物清理：解压目录与下载 zip 不残留
        Assert.Empty(Directory.GetDirectories(Path.Combine(_tempDir.Path, "tools", "hpatchz"), "*.extracting"));
        Assert.False(File.Exists(toolPath + ".zip") && File.Exists(Path.Combine(
            Path.GetDirectoryName(toolPath)!, Path.GetFileName(toolPath) + ".zip")));
    }

    [Fact]
    public async Task EnsureAvailable_AlreadyReady_ZeroNetwork()
    {
        // 幂等：已就绪（目标存在且可执行）直接返回，不发任何网络请求
        var provisioner = CreateProvisioner();
        var zip = TestZip.Create(("linux64/hpatchz", "x"u8.ToArray()));
        _downloader.Responses[HpatchzProvisioner.BuildDownloadUrl("linux64.zip")] = zip;
        var toolPath = await provisioner.EnsureAvailableAsync();

        _downloader.Requests.Clear();
        var again = await provisioner.EnsureAvailableAsync();

        Assert.Equal(toolPath, again);
        Assert.Empty(_downloader.Requests);
    }

    [Fact]
    public async Task EnsureAvailable_Md5Mismatch_FoldsToUpdateException()
    {
        // 校验链走真下载器 + 桩 HTTP（FakeDownloader 不做 MD5 校验）：资产与嵌入 MD5 不符
        // （官方 release 被替换/CDN 损坏）按 UpdateException 折算，消息含版本上下文
        var handler = new StubHttpHandler();
        handler.Map(HpatchzProvisioner.BuildDownloadUrl("linux64.zip"), "tampered-or-corrupt");
        var provisioner = new HpatchzProvisioner(
            new HttpFileDownloader(new HttpClient(handler), new HttpFileDownloaderOptions { MaxAttempts = 1 }),
            dataDirectory: _tempDir.Path);

        var ex = await Assert.ThrowsAsync<UpdateException>(() => provisioner.EnsureAvailableAsync());

        Assert.Contains("hpatchz", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectAsset_CoversMeasuredPlatformsOnly()
    {
        // 仅覆盖有实测 MD5 的组合（2026-10-02 官方 v5.1.3 实测：linux64/windows64）；
        // 其余平台/架构返回 null——EnsureAvailable 对 null 给「手动安装」可操作提示
        Assert.Equal(("linux64.zip", "1b66f06fea325eaa5e1f96c881c10459"),
            HpatchzProvisioner.SelectAsset(isLinux: true, System.Runtime.InteropServices.Architecture.X64));
        Assert.Equal(("windows64.zip", "ec432fd2e20e9a6f8449aae9a9850d86"),
            HpatchzProvisioner.SelectAsset(isLinux: false, System.Runtime.InteropServices.Architecture.X64));
        Assert.Null(HpatchzProvisioner.SelectAsset(isLinux: true, System.Runtime.InteropServices.Architecture.Arm64));
        Assert.Null(HpatchzProvisioner.SelectAsset(isLinux: false, System.Runtime.InteropServices.Architecture.Arm64));
    }

    [Fact]
    public void BuildDownloadUrl_PointsAtOfficialRelease()
    {
        Assert.Equal(
            "https://github.com/sisong/HDiffPatch/releases/download/v5.1.3/hdiffpatch_v5.1.3_bin_linux64.zip",
            HpatchzProvisioner.BuildDownloadUrl("linux64.zip"));
    }
}
