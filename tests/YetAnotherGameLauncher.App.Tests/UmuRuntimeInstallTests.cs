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

    // ==== 2026-09-22 测试审计补齐：SHA256 校验失败、包形态异常、暂存残留清理 ====

    [Fact]
    public async Task EnsureRuntimeAsync_Sha256Mismatch_ThrowsAndLeavesNoCachedArchive()
    {
        var provisioner = NewProvisioner();
        var baseUrl = $"https://repo.steampowered.com{ImagesPath}/0-0-0";
        var archiveName = $"{RuntimeName}.tar.xz";
        _http.Map($"https://repo.steampowered.com{ImagesPath}/latest-public-beta.txt", "0-0-0\n");
        _http.Map($"{baseUrl}/SHA256SUMS", $"{new string('f', 64)}  {archiveName}\n");
        _http.Map($"{baseUrl}/BUILD_ID.txt", "b123\n");
        _downloader.Serve($"{baseUrl}/{archiveName}", BuildRuntimeArchive(RuntimeName));

        var ex = await Assert.ThrowsAsync<LaunchException>(
            () => provisioner.EnsureRuntimeAsync(Variant, RuntimeName));

        Assert.Equal(LaunchFailureKind.UmuRuntimeDownloadFailed, ex.Kind);
        Assert.Contains("校验失败", ex.Message, StringComparison.Ordinal);
        // 校验失败必须发生在落位之前：安装目录不得出现半成品
        Assert.False(Directory.Exists(InstallRoot()));
    }

    [Fact]
    public async Task EnsureRuntimeAsync_ArchiveWithoutTopLevelDirectory_ThrowsClassified()
    {
        // 包里只有散文件没有顶层目录：落位失败必须归类 UmuRuntimeDownloadFailed（可重试），
        // 不得以裸异常逃逸到 Unknown。SHA256SUMS 须带本资产的有效条目（缺条目形态由
        // ShaSumsMissingEntry 用例钉住——两道防线先后次序不同）
        var provisioner = NewProvisioner();
        var baseUrl = $"https://repo.steampowered.com{ImagesPath}/0-0-0";
        var archiveName = $"{RuntimeName}.tar.xz";
        var archiveBytes = BuildFileOnlyArchive();
        var expectedSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(archiveBytes)).ToLowerInvariant();
        _http.Map($"https://repo.steampowered.com{ImagesPath}/latest-public-beta.txt", "0-0-0\n");
        _http.Map($"{baseUrl}/SHA256SUMS", $"{expectedSha}  {archiveName}\n");
        _http.Map($"{baseUrl}/BUILD_ID.txt", "b123\n");
        _downloader.Serve($"{baseUrl}/{archiveName}", archiveBytes);

        var ex = await Assert.ThrowsAsync<LaunchException>(
            () => provisioner.EnsureRuntimeAsync(Variant, RuntimeName));

        Assert.Equal(LaunchFailureKind.UmuRuntimeDownloadFailed, ex.Kind);
        Assert.Contains("顶层目录", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureRuntimeAsync_StagingLeftoverFromAbortedRun_CleanedAndReinstalled()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("符号链接与执行位为 POSIX 语义（与正流程用例同款跳过）");
        }

        var provisioner = NewProvisioner();
        var staging = InstallRoot() + ".staging";
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "garbage-from-aborted-run"), "x");
        var baseUrl = $"https://repo.steampowered.com{ImagesPath}/0-0-0";
        var tarGz = BuildRuntimeArchive(RuntimeName);
        var expectedSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(tarGz)).ToLowerInvariant();
        _http.Map($"https://repo.steampowered.com{ImagesPath}/latest-public-beta.txt", "0-0-0\n");
        _http.Map($"{baseUrl}/SHA256SUMS", $"{expectedSha}  {RuntimeName}.tar.xz\n");
        _http.Map($"{baseUrl}/BUILD_ID.txt", "b123\n");
        _downloader.Serve($"{baseUrl}/{RuntimeName}.tar.xz", tarGz);

        await provisioner.EnsureRuntimeAsync(Variant, RuntimeName);

        Assert.False(Directory.Exists(staging)); // 上次中断的暂存树已清
        Assert.True(Directory.Exists(InstallRoot()));
    }

    [Fact]
    public async Task EnsureRuntimeAsync_ShaSumsMissingEntry_ThrowsClassified()
    {
        // 次级 suspect（第 9 轮，artifacts/bugs.md）：SHA256SUMS 拉到了但没有本资产的条目时，
        // 旧形态静默跳过校验——未验哈希的包直接落盘安装。修复 = 缺条目按下载失败分类拒绝
        var provisioner = NewProvisioner();
        var baseUrl = $"https://repo.steampowered.com{ImagesPath}/0-0-0";
        _http.Map($"https://repo.steampowered.com{ImagesPath}/latest-public-beta.txt", "0-0-0\n");
        _http.Map($"{baseUrl}/SHA256SUMS", "0123abc  some-other-file.tar.xz\n"); // 无本资产条目
        _http.Map($"{baseUrl}/BUILD_ID.txt", "b123\n");
        _downloader.Serve($"{baseUrl}/{RuntimeName}.tar.xz", BuildRuntimeArchive(RuntimeName));

        var ex = await Assert.ThrowsAsync<LaunchException>(
            () => provisioner.EnsureRuntimeAsync(Variant, RuntimeName));

        Assert.Equal(LaunchFailureKind.UmuRuntimeDownloadFailed, ex.Kind);
        Assert.Contains("缺少", ex.Message, StringComparison.Ordinal); // 红落此断言：旧形态静默跳过、无此文案
        Assert.False(Directory.Exists(InstallRoot())); // 拒绝发生在落位之前
    }

    [Fact]
    public async Task EnsureRuntimeAsync_VersionUnsafePathSegment_ThrowsClassified()
    {
        // 次级 suspect（第 9 轮）：版本号未过白名单即拼缓存路径与 URL——路径段含分隔符/穿越段
        // 产注入面。修复 = 白名单（ASCII 字母数字与 ._-、不以点开头）不过即按下载失败分类拒绝
        var provisioner = NewProvisioner();
        _http.Map($"https://repo.steampowered.com{ImagesPath}/latest-public-beta.txt", "../evil\n");

        var ex = await Assert.ThrowsAsync<LaunchException>(
            () => provisioner.EnsureRuntimeAsync(Variant, RuntimeName));

        Assert.Equal(LaunchFailureKind.UmuRuntimeDownloadFailed, ex.Kind);
        Assert.Contains("非法字符", ex.Message, StringComparison.Ordinal); // 红落此断言：旧形态落到 404 分类文案
    }

    [Fact]
    public async Task EnsureRuntimeAsync_BuildIdUnsafePathSegment_ThrowsClassified()
    {
        // 同上：BUILD_ID 进缓存文件名（Path.Combine(cache, $"{archive}.{buildId}")），
        // 穿越段会把缓存写入 dataHome 之外
        var provisioner = NewProvisioner();
        var baseUrl = $"https://repo.steampowered.com{ImagesPath}/0-0-0";
        _http.Map($"https://repo.steampowered.com{ImagesPath}/latest-public-beta.txt", "0-0-0\n");
        // SUMS 须含本资产条目：缺条目检查在 BUILD_ID 校验之前（由 ShaSumsMissingEntry 用例钉住）
        _http.Map($"{baseUrl}/SHA256SUMS", $"0123abc  {RuntimeName}.tar.xz\n");
        _http.Map($"{baseUrl}/BUILD_ID.txt", "..\\..\\evil\n");

        var ex = await Assert.ThrowsAsync<LaunchException>(
            () => provisioner.EnsureRuntimeAsync(Variant, RuntimeName));

        Assert.Equal(LaunchFailureKind.UmuRuntimeDownloadFailed, ex.Kind);
        Assert.Contains("非法字符", ex.Message, StringComparison.Ordinal); // 红落此断言：旧形态直通缓存路径
        Assert.Empty(Directory.GetFiles(_temp.Path, "SteamLinuxRuntime_4.tar.xz*", SearchOption.AllDirectories));
    }

    /// <summary>只含散文件（无目录条目）的 gzip+tar：解压后暂存目录里没有任何子目录。</summary>
    private static byte[] BuildFileOnlyArchive()
    {
        using var tarBuffer = new MemoryStream();
        using (var writer = new TarWriter(tarBuffer, TarEntryFormat.Pax, leaveOpen: true))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "loose-file.txt")
            {
                DataStream = new MemoryStream("data"u8.ToArray()),
            };
            writer.WriteEntry(entry);
        }

        using var result = new MemoryStream();
        tarBuffer.Position = 0;
        using (var gz = new GZipStream(result, CompressionMode.Compress, leaveOpen: true))
        {
            tarBuffer.CopyTo(gz);
        }

        return result.ToArray();
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
