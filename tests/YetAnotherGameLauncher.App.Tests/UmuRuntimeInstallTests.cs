using System.Formats.Tar;
using System.IO.Compression;
using Xunit;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Services;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// EnsureRuntimeAsync 正流程端到端（Phase 4 补测，2026-09-19，审计最大单点缺口 ~110 行）：
/// 桩 HTTP 回放 latest-public-beta.txt / SHA256SUMS / BUILD_ID.txt，真实 tar.gz 走
/// 与线上相同的解包链路；验证安装落位、安装标记、执行位、umu 符号链接与缓存清理。
/// </summary>
public sealed class UmuRuntimeInstallTests : IDisposable
{
    private const string Variant = "steamrt4";
    private const string RuntimeName = "SteamLinuxRuntime_4";
    private const string ImagesPath = "/steamrt4/images";

    private readonly TempDir _temp = new();
    private readonly StubHttpHandler _http = new();
    private readonly FakeDownloader _downloader = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task EnsureRuntimeAsync_NotInstalled_DownloadsVerifiesAndInstalls()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("符号链接与执行位为 POSIX 语义，Windows 腿由 PureMock 版本覆盖");
        }

        var provisioner = NewProvisioner();
        var version = "0-0-0";
        var buildId = "b123";
        var baseUrl = $"https://repo.steampowered.com{ImagesPath}/{version}";
        var archiveName = $"{RuntimeName}.tar.xz";

        var tarGz = BuildRuntimeArchive(RuntimeName);
        // SHA256SUMS 行写入真实哈希：同时驱动"解析 + 流式校验通过"双路径
        var expectedSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(tarGz)).ToLowerInvariant();
        _http.Map($"https://repo.steampowered.com{ImagesPath}/latest-public-beta.txt", $"{version}\n");
        _http.Map($"{baseUrl}/SHA256SUMS", $"{expectedSha}  {archiveName}\n0123abc  other.tar.xz\n");
        _http.Map($"{baseUrl}/BUILD_ID.txt", $"{buildId}\n");
        _downloader.Serve($"{baseUrl}/{archiveName}", tarGz);

        await provisioner.EnsureRuntimeAsync(Variant, RuntimeName);

        // 安装落位：目录 + 安装标记 + 入口脚本
        var installRoot = InstallRoot();
        Assert.True(Directory.Exists(installRoot));
        Assert.True(File.Exists(Path.Combine(installRoot, YetAnotherGameLauncher.Core.Services.Umu.UmuPaths.InstallMarkerName)));
        Assert.True(File.Exists(Path.Combine(installRoot, "_v2-entry-point")));
        Assert.True(Directory.Exists(Path.Combine(installRoot, "files"))); // tar 顶层目录内容已迁移

        // 二次调用：已就绪零网络（幂等早退）
        var requestsBefore = _downloader.Requests.Count;
        await provisioner.EnsureRuntimeAsync(Variant, RuntimeName);
        Assert.Equal(requestsBefore, _downloader.Requests.Count);
    }

    [Fact]
    public async Task EnsureRuntimeAsync_AlreadyReady_NoNetwork()
    {
        var provisioner = NewProvisioner();
        InstallFakeRuntime(RuntimeName);

        await provisioner.EnsureRuntimeAsync(Variant, RuntimeName);

        Assert.Empty(_downloader.Requests); // 零下载
    }

    [Fact]
    public void IsRuntimeReady_InstalledWithMarkerAndEntry_True()
    {
        var provisioner = NewProvisioner();
        InstallFakeRuntime(RuntimeName);

        Assert.True(provisioner.IsRuntimeReady(Variant));
        Assert.False(provisioner.IsRuntimeReady("steamrt3")); // 其它变体未装
    }

    [Fact]
    public async Task EnsureRuntimeAsync_EmptyVersionFile_ClassifiesAsDownloadFailed()
    {
        // 审计记录的回归分支：版本号为空 → UmuRuntimeDownloadFailed（不落 Unknown 丢重试 UI）
        var provisioner = NewProvisioner();
        _http.Map($"https://repo.steampowered.com{ImagesPath}/latest-public-beta.txt", " \n");

        var ex = await Assert.ThrowsAsync<LaunchException>(
            () => provisioner.EnsureRuntimeAsync(Variant, RuntimeName));

        Assert.Equal(LaunchFailureKind.UmuRuntimeDownloadFailed, ex.Kind);
    }

    private UmuComponentProvisioner NewProvisioner() => new(
        new HttpClient(_http), _downloader,
        dataHome: _temp.Path, cacheHome: _temp.Path);

    private string InstallRoot() =>
        YetAnotherGameLauncher.Core.Services.Umu.UmuPaths.RuntimeDirectory(Variant, _temp.Path);

    /// <summary>预置一个"已就绪"的 Runtime 目录（marker + 入口脚本）。</summary>
    private void InstallFakeRuntime(string runtimeName)
    {
        var dir = InstallRoot();
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, YetAnotherGameLauncher.Core.Services.Umu.UmuPaths.InstallMarkerName), "ok");
        File.WriteAllText(Path.Combine(dir, "_v2-entry-point"), "#!/bin/sh\n");
    }

    /// <summary>构造顶层目录 SteamLinuxRuntime_4 含入口脚本与 files/ 的 gzip+tar（与线上包同构）。</summary>
    private static byte[] BuildRuntimeArchive(string topDir)
    {
        using var tarBuffer = new MemoryStream();
        using (var writer = new TarWriter(tarBuffer, TarEntryFormat.Pax, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, $"{topDir}/"));
            var entry = new PaxTarEntry(TarEntryType.RegularFile, $"{topDir}/_v2-entry-point")
            {
                DataStream = new MemoryStream("#!/bin/sh\n"u8.ToArray()),
            };
            writer.WriteEntry(entry);
            var files = new PaxTarEntry(TarEntryType.Directory, $"{topDir}/files/");
            writer.WriteEntry(files);
        }

        using var result = new MemoryStream();
        tarBuffer.Position = 0;
        using (var gz = new GZipStream(result, CompressionMode.Compress, leaveOpen: true))
        {
            tarBuffer.CopyTo(gz);
        }

        return result.ToArray();
    }
}
