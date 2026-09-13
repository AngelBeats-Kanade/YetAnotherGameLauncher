using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using YetAnotherGameLauncher.Core.Services.Umu;
using YetAnotherGameLauncher.Services;
using YetAnotherGameLauncher.TestSupport;
using Xunit;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 原生 umu 组件准备器：三代号（DW/GE/UMU-Proton）latest 下载的源分流与资产选择 +
/// SHA256SUMS 解析。网络面全部替身（StubHttpHandler + FakeDownloader）；
/// DW 源是 dawn.wine Forgejo（数组根），GE/UMU 是 GitHub（对象根）。
/// </summary>
public sealed class UmuComponentProvisionerTests : IDisposable
{
    private readonly TempDir _tempDir = new();
    private readonly StubHttpHandler _http = new();
    private readonly FakeDownloader _downloader = new();
    private readonly UmuComponentProvisioner _provisioner;

    public UmuComponentProvisionerTests()
    {
        _provisioner = new UmuComponentProvisioner(
            new HttpClient(_http), _downloader, dataHome: _tempDir.Path, cacheHome: _tempDir.Path);
    }

    public void Dispose() => _tempDir.Dispose();

    [Fact]
    public async Task EnsureProtonAsync_DWCodename_DownloadsForgejoLatestAndStripsArchSuffix()
    {
        // Forgejo 返回数组根；.torrent/.sha512sum 是干扰资产，必须跳过
        ServeForgejoReleases([
            ("dwproton-11.0-99-x86_64.tar.xz", "https://dawn.wine/x/dwproton-11.0-99-x86_64.tar.xz"),
            ("dwproton-11.0-99-x86_64.tar.xz.torrent", "https://dawn.wine/x/torrent"),
            ("dwproton-11.0-99-x86_64.sha512sum", "https://dawn.wine/x/sum"),
        ]);
        _downloader.Serve(
            "https://dawn.wine/x/dwproton-11.0-99-x86_64.tar.xz",
            BuildProtonArchive("dwproton-11.0-99-x86_64"));

        var path = await _provisioner.EnsureProtonAsync("DW-Proton");

        // 资产名去掉扩展与架构后缀作为安装目录名（与 GE/UMU 的版本目录命名对齐）
        Assert.EndsWith(
            Path.Combine("compatibilitytools.d", "dwproton-11.0-99"),
            path.Replace('\\', '/').Replace(Path.DirectorySeparatorChar, '/'));
        Assert.True(File.Exists(Path.Combine(path, "proton")));
        Assert.True(File.Exists(Path.Combine(path, "toolmanifest.vdf")));
    }

    [Fact]
    public async Task EnsureProtonAsync_GECodename_UsesGitHubLatestObjectJson()
    {
        ServeGitHubRelease(UmuComponentProvisioner.GeProtonReleaseApi, "GE-Proton10-99", [
            ("GE-Proton10-99.tar.gz", "https://github.com/x/GE-Proton10-99.tar.gz"),
            ("GE-Proton10-99.sha512sum", "https://github.com/x/sum"),
        ]);
        _downloader.Serve(
            "https://github.com/x/GE-Proton10-99.tar.gz",
            BuildProtonArchive("GE-Proton10-99"));

        var path = await _provisioner.EnsureProtonAsync("GE-Proton");

        Assert.EndsWith(
            Path.Combine("compatibilitytools.d", "GE-Proton10-99"),
            path.Replace('\\', '/').Replace(Path.DirectorySeparatorChar, '/'));
        Assert.True(_provisioner.IsProtonReady(path));
    }

    [Fact]
    public async Task EnsureProtonAsync_UMUCodename_UsesUmuRepoLatest()
    {
        ServeGitHubRelease(UmuComponentProvisioner.UmuProtonReleaseApi, "UMU-Proton-10.0-9", [
            ("UMU-Proton-10.0-9.tar.gz", "https://github.com/x/UMU-Proton-10.0-9.tar.gz"),
        ]);
        _downloader.Serve(
            "https://github.com/x/UMU-Proton-10.0-9.tar.gz",
            BuildProtonArchive("UMU-Proton-10.0-9"));

        var path = await _provisioner.EnsureProtonAsync("UMU-Proton");

        Assert.EndsWith(
            Path.Combine("compatibilitytools.d", "UMU-Proton-10.0-9"),
            path.Replace('\\', '/').Replace(Path.DirectorySeparatorChar, '/'));
        Assert.True(_provisioner.IsProtonReady(path));
    }

    [Fact]
    public async Task EnsureProtonAsync_CodenameDownloadFails_FallsBackToInstalledLatest()
    {
        // 本地已有该发行版旧版：latest 下载失败（API 未注册 → 404）时离线回退，不静默换发行版
        InstallReadyProton("GE-Proton10-9");

        var path = await _provisioner.EnsureProtonAsync("GE-Proton");

        Assert.EndsWith(
            Path.Combine("compatibilitytools.d", "GE-Proton10-9"),
            path.Replace('\\', '/').Replace(Path.DirectorySeparatorChar, '/'));
    }

    [Fact]
    public async Task EnsureProtonAsync_DWCodename_FallsBackToLocalDwproton()
    {
        InstallReadyProton("dwproton-11.0-12");

        var path = await _provisioner.EnsureProtonAsync("DW-Proton");

        Assert.EndsWith(
            Path.Combine("compatibilitytools.d", "dwproton-11.0-12"),
            path.Replace('\\', '/').Replace(Path.DirectorySeparatorChar, '/'));
    }

    [Fact]
    public void ParseSha256For_FindsMatchingArchiveLine()
    {
        const string sums = """
            abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789  SteamLinuxRuntime_sniper.tar.xz
            1111111111111111111111111111111111111111111111111111111111111111  other.tar.xz
            """;
        var sha = UmuComponentProvisioner.ParseSha256For(sums, "SteamLinuxRuntime_sniper.tar.xz");
        Assert.Equal("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", sha);
    }

    [Fact]
    public void ParseSha256For_MissingFile_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, UmuComponentProvisioner.ParseSha256For("deadbeef  a.tar.xz\n", "b.tar.xz"));
    }

    /// <summary>在 compatibilitytools.d 下放一个"就绪"的 Proton 目录（toolmanifest.vdf + proton）。</summary>
    private void InstallReadyProton(string name)
    {
        var dir = Path.Combine(UmuPaths.SteamCompatRoot(_tempDir.Path), name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "toolmanifest.vdf"), "\"manifest\" { }");
        File.WriteAllText(Path.Combine(dir, "proton"), "#!/bin/sh\n");
    }

    /// <summary>注册 Forgejo releases 数组响应（dawn.wine，数组根，取首个非 draft/prerelease）。</summary>
    private void ServeForgejoReleases((string Name, string Url)[] assets)
    {
        var json = JsonSerializer.Serialize(new object[]
        {
            new
            {
                tag_name = "dwproton-11.0-99",
                draft = false,
                prerelease = false,
                assets = assets.Select(a => new { name = a.Name, browser_download_url = a.Url }),
            },
        });
        _http.Map(UmuComponentProvisioner.DwProtonReleaseApi, json);
    }

    /// <summary>注册 GitHub latest release 对象响应。</summary>
    private void ServeGitHubRelease(string api, string tag, (string Name, string Url)[] assets)
    {
        var json = JsonSerializer.Serialize(new
        {
            tag_name = tag,
            assets = assets.Select(a => new { name = a.Name, browser_download_url = a.Url }),
        });
        _http.Map(api, json);
    }

    /// <summary>
    /// 构造顶层目录含 toolmanifest.vdf 与 proton 的 gzip+tar（IsProtonReady 认这两份文件）。
    /// 解压按魔数而非扩展名分发，故 DW 的 .tar.xz 资产名配 gzip 内容即可（SharpCompress 无 XZ 编码器，
    /// 与 UmuArchiveExtractionTests 同一取舍）。
    /// </summary>
    private static byte[] BuildProtonArchive(string topDir)
    {
        using var tarBuffer = new MemoryStream();
        using (var writer = new TarWriter(tarBuffer, TarEntryFormat.Pax, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, $"{topDir}/"));
            var manifest = new PaxTarEntry(TarEntryType.RegularFile, $"{topDir}/toolmanifest.vdf")
            {
                DataStream = new MemoryStream("\"manifest\" { }"u8.ToArray()),
            };
            writer.WriteEntry(manifest);
            var proton = new PaxTarEntry(TarEntryType.RegularFile, $"{topDir}/proton")
            {
                DataStream = new MemoryStream("#!/bin/sh\n"u8.ToArray()),
            };
            writer.WriteEntry(proton);
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
