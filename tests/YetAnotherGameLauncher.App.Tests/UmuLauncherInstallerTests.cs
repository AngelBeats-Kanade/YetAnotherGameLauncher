using System.Text;
using System.Text.Json;
using YetAnotherGameLauncher.Services;
using Xunit;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// umu-launcher 引导安装：从 GitHub release 元数据挑选 zipapp 资产 → 下载 → 解出 umu-run
/// → 落到应用数据目录并补可执行位。网络面全部用替身（StubHttpHandler + FakeDownloader）。
/// </summary>
public sealed class UmuLauncherInstallerTests : IDisposable
{
    private readonly TempDir _tempDir = new();
    private readonly StubHttpHandler _http = new();
    private readonly FakeDownloader _downloader = new();
    private readonly UmuLauncherInstaller _installer;

    public UmuLauncherInstallerTests()
    {
        _installer = new UmuLauncherInstaller(new HttpClient(_http), _downloader);
    }

    public void Dispose() => _tempDir.Dispose();

    [Fact]
    public async Task InstallLatestAsync_DownloadsZipappAndExtractsUmuRun()
    {
        // GitHub API 返回资产清单（含 deb/rpm 干扰项），下载面给一个含 umu-run 的 tar
        ServeReleaseApi("1.4.4", [
            ("python3-umu-launcher_1.4.4-1_amd64_debian-12.deb", "https://github.com/x/deb"),
            ("umu-launcher-1.4.4-zipapp.tar", "https://github.com/x/zipapp.tar"),
            ("umu-launcher-1.4.4.fc43.x86_64.rpm", "https://github.com/x/rpm"),
        ]);
        _downloader.Serve("https://github.com/x/zipapp.tar", BuildTarWithUmuRun());

        var installed = await _installer.InstallLatestAsync(_tempDir.Path);

        Assert.Equal(Path.Combine(_tempDir.Path, "umu-run"), installed);
        Assert.True(File.Exists(installed));
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(
                File.GetUnixFileMode(installed).HasFlag(System.IO.UnixFileMode.UserExecute),
                "umu-run 必须带可执行位");
        }
    }

    [Fact]
    public async Task InstallLatestAsync_NoZipappAsset_ThrowsFriendlyError()
    {
        ServeReleaseApi("1.4.4", [("umu-launcher-1.4.4.fc43.x86_64.rpm", "https://github.com/x/rpm")]);

        var ex = await Assert.ThrowsAsync<YetAnotherGameLauncher.Core.Abstractions.UpdateException>(
            () => _installer.InstallLatestAsync(_tempDir.Path));

        Assert.Contains("zipapp", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallLatestAsync_ApiError_ThrowsFriendlyError()
    {
        // 未注册任何 API 响应 → StubHttpHandler 返回 404
        await Assert.ThrowsAsync<YetAnotherGameLauncher.Core.Abstractions.UpdateException>(
            () => _installer.InstallLatestAsync(_tempDir.Path));
    }

    [Fact]
    public void SelectZipappAssetUrl_PicksZipappAmongPackages()
    {
        var url = UmuLauncherInstaller.SelectZipappAssetUrl([
            ("python3-umu-launcher_1.4.4-1_amd64_debian-12.deb", "u1"),
            ("umu-launcher-1.4.4-zipapp.tar", "u2"),
            ("umu-launcher_1.4.4-1_all.deb", "u3"),
        ]);

        Assert.Equal("u2", url);
    }

    [Fact]
    public void SelectZipappAssetUrl_Missing_ReturnsNull()
    {
        Assert.Null(UmuLauncherInstaller.SelectZipappAssetUrl([
            ("umu-launcher-1.4.4.fc43.x86_64.rpm", "u1"),
        ]));
    }

    /// <summary>注册 release API 响应（下一次对 api.github.com 的 GET 返回这份 JSON）。</summary>
    private void ServeReleaseApi(string tag, (string Name, string Url)[] assets)
    {
        var json = JsonSerializer.Serialize(new
        {
            tag_name = tag,
            assets = assets.Select(a => new { name = a.Name, browser_download_url = a.Url }),
        });
        _http.Map(UmuLauncherInstaller.ReleaseApiUrl, json);
    }

    /// <summary>手工构造一个内含 umu-run 条目的最小 ustar 归档（512 头 + 内容 + 补齐 + 结束块）。</summary>
    private static byte[] BuildTarWithUmuRun()
    {
        const int blockSize = 512;
        var content = "#!/usr/bin/env python3\n# umu zipapp stub\n"u8.ToArray();
        var padded = (content.Length + blockSize - 1) / blockSize * blockSize;

        var tar = new byte[blockSize + padded + 2 * blockSize];
        var name = "umu-run"u8.ToArray();
        Array.Copy(name, tar, name.Length);
        WriteOctal(tar, 100, 8, 420);     // mode：0644（十进制 420）
        WriteOctal(tar, 108, 8, 0);       // uid
        WriteOctal(tar, 116, 8, 0);       // gid
        WriteOctal(tar, 124, 12, content.Length); // size
        WriteOctal(tar, 136, 12, 0);      // mtime
        tar[156] = (byte)'0';             // typeflag: 普通文件
        "ustar\0"u8.ToArray().CopyTo(tar, 257);     // magic
        "00"u8.ToArray().CopyTo(tar, 263);          // version

        // checksum：把 chksum 字段当空格求和，再写回 6 位八进制 + NUL + 空格
        for (var i = 148; i < 156; i++)
        {
            tar[i] = (byte)' ';
        }

        var checksum = tar.Aggregate(0, (sum, b) => sum + b);
        WriteOctal(tar, 148, 7, checksum);

        content.CopyTo(tar, blockSize);
        return tar;

        static void WriteOctal(byte[] buffer, int offset, int width, long value)
        {
            var text = Convert.ToString(value, 8).PadLeft(width - 1, '0') + "\0";
            for (var i = 0; i < width; i++)
            {
                buffer[offset + i] = (byte)text[i];
            }
        }
    }
}
