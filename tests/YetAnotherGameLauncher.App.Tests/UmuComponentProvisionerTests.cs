using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Services.Umu;
using YetAnotherGameLauncher.Services;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 原生 umu 组件准备器：三代号（DW/GE/UMU-Proton）latest 下载的源分流与资产选择 +
/// 架构过滤（资产名后缀 + wineserver ELF 兜底）、更新清理旧版、SHA256SUMS 解析。
/// 网络面全部替身（StubHttpHandler + FakeDownloader）；
/// DW 源是 dawn.wine Forgejo（数组根），GE/UMU 是 GitHub（对象根）。
/// </summary>
public sealed class UmuComponentProvisionerTests : IDisposable
{
    /// <summary>ELF e_machine：x86-64（与被测常量 0x3E 对应）。</summary>
    private const ushort ElfX86_64 = 0x3E;

    /// <summary>ELF e_machine：aarch64（与被测常量 0xB7 对应）。</summary>
    private const ushort ElfAarch64 = 0xB7;

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

    /// <summary>按注入架构构造准备器（架构过滤与 ELF 兜底的确定性测试基准）。</summary>
    private UmuComponentProvisioner NewProvisioner(Architecture hostArchitecture) =>
        new(new HttpClient(_http), _downloader,
            dataHome: _tempDir.Path, cacheHome: _tempDir.Path, hostArchitecture: hostArchitecture);

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
            NormalizeSeparators(Path.Combine("compatibilitytools.d", "dwproton-11.0-99")),
            NormalizeSeparators(path));
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
            NormalizeSeparators(Path.Combine("compatibilitytools.d", "GE-Proton10-99")),
            NormalizeSeparators(path));
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
            NormalizeSeparators(Path.Combine("compatibilitytools.d", "UMU-Proton-10.0-9")),
            NormalizeSeparators(path));
        Assert.True(_provisioner.IsProtonReady(path));
    }

    [Fact]
    public async Task EnsureProtonAsync_LocalInstalled_ReturnsWithoutNetwork()
    {
        // 本地已有该发行版旧版：启动/组件准备直接用本地，不联网拉 latest（更新走 UpdateProtonAsync）
        InstallReadyProton("GE-Proton10-9");

        var path = await _provisioner.EnsureProtonAsync("GE-Proton");

        Assert.EndsWith(
            NormalizeSeparators(Path.Combine("compatibilitytools.d", "GE-Proton10-9")),
            NormalizeSeparators(path));
        Assert.Empty(_downloader.Requests); // 未发起任何下载
    }

    [Fact]
    public async Task EnsureProtonAsync_DWCodename_UsesLocalDwproton()
    {
        InstallReadyProton("dwproton-11.0-12");

        var path = await _provisioner.EnsureProtonAsync("DW-Proton");

        Assert.EndsWith(
            NormalizeSeparators(Path.Combine("compatibilitytools.d", "dwproton-11.0-12")),
            NormalizeSeparators(path));
    }

    [Fact]
    public void SelectTarAsset_DualArchAssets_PrefersHostArchSuffix()
    {
        // GitHub 资产顺序 = 上传顺序：GE-Proton 曾把 aarch64 排在 x86_64 之前，取"第一个"必错
        var release = JsonDocument.Parse("""
            {
              "assets": [
                { "name": "GE-Proton11-6-aarch64.sha512sum", "browser_download_url": "https://x/sum1" },
                { "name": "GE-Proton11-6-aarch64.tar.gz", "browser_download_url": "https://x/arm" },
                { "name": "GE-Proton11-6-x86_64.sha512sum", "browser_download_url": "https://x/sum2" },
                { "name": "GE-Proton11-6-x86_64.tar.gz", "browser_download_url": "https://x/x64" }
              ]
            }
            """).RootElement;

        Assert.Equal("GE-Proton11-6-x86_64.tar.gz",
            UmuComponentProvisioner.SelectTarAsset(release, "GE-Proton", "-x86_64").Name);
        Assert.Equal("GE-Proton11-6-aarch64.tar.gz",
            UmuComponentProvisioner.SelectTarAsset(release, "GE-Proton", "-aarch64").Name);
    }

    [Fact]
    public void SelectTarAsset_UnsuffixedAsset_WorksWithAnyHost()
    {
        // UMU-Proton 单架构发布形态：无后缀资产对任意主机可用
        var release = JsonDocument.Parse("""
            {
              "assets": [
                { "name": "UMU-Proton-10.0-4.tar.gz", "browser_download_url": "https://x/umu" }
              ]
            }
            """).RootElement;

        Assert.Equal("UMU-Proton-10.0-4.tar.gz",
            UmuComponentProvisioner.SelectTarAsset(release, "UMU-Proton", "-x86_64").Name);
        Assert.Equal("UMU-Proton-10.0-4.tar.gz",
            UmuComponentProvisioner.SelectTarAsset(release, "UMU-Proton", "-aarch64").Name);
    }

    [Fact]
    public void SelectTarAsset_AssetNameWithPathCharacters_Rejected()
    {
        // 回归：release JSON 的 name 会直接拼进缓存/安装路径——含路径分隔符、
        // ".." 段或首点的名字必须在选择阶段拒绝（上游 release 被篡改时的纵深防御）
        var release = JsonDocument.Parse("""
            {
              "assets": [
                { "name": "../evil.tar.gz", "browser_download_url": "https://x/evil" },
                { "name": "..hidden.tar.gz", "browser_download_url": "https://x/hide" },
                { "name": "GE-Proton11-6.tar.gz", "browser_download_url": "https://x/ok" }
              ]
            }
            """).RootElement;

        Assert.Equal("GE-Proton11-6.tar.gz",
            UmuComponentProvisioner.SelectTarAsset(release, "GE-Proton", "-x86_64").Name);
    }

    [Fact]
    public void SelectTarAsset_OnlyOppositeArch_ReturnsEmpty()
    {
        // 只有相反架构资产时不得下载（调用方据此报"无适配架构资产"）
        var release = JsonDocument.Parse("""
            {
              "assets": [
                { "name": "GE-Proton11-6-aarch64.tar.gz", "browser_download_url": "https://x/arm" }
              ]
            }
            """).RootElement;

        var asset = UmuComponentProvisioner.SelectTarAsset(release, "GE-Proton", "-x86_64");
        Assert.True(string.IsNullOrEmpty(asset.Url)); // 调用方按"无适配资产"报错，不得下载反向架构
    }

    [Fact]
    public async Task EnsureProtonAsync_GECodename_DualAssets_DownloadsHostArchAsset()
    {
        ServeGitHubRelease(UmuComponentProvisioner.GeProtonReleaseApi, "GE-Proton11-6", [
            ("GE-Proton11-6-aarch64.tar.gz", "https://github.com/x/GE-Proton11-6-aarch64.tar.gz"),
            ("GE-Proton11-6-x86_64.tar.gz", "https://github.com/x/GE-Proton11-6-x86_64.tar.gz"),
        ]);
        _downloader.Serve(
            "https://github.com/x/GE-Proton11-6-x86_64.tar.gz",
            BuildProtonArchive("GE-Proton11-6-x86_64", wineserverElfMachine: ElfX86_64));

        var path = await NewProvisioner(Architecture.X64).EnsureProtonAsync("GE-Proton");

        Assert.EndsWith(
            NormalizeSeparators(Path.Combine("compatibilitytools.d", "GE-Proton11-6")),
            NormalizeSeparators(path));
        Assert.Equal(["https://github.com/x/GE-Proton11-6-x86_64.tar.gz"], _downloader.Requests);
    }

    [Fact]
    public async Task EnsureProtonAsync_GECodename_OnArm64Host_DownloadsAarch64Asset()
    {
        ServeGitHubRelease(UmuComponentProvisioner.GeProtonReleaseApi, "GE-Proton11-6", [
            ("GE-Proton11-6-aarch64.tar.gz", "https://github.com/x/GE-Proton11-6-aarch64.tar.gz"),
            ("GE-Proton11-6-x86_64.tar.gz", "https://github.com/x/GE-Proton11-6-x86_64.tar.gz"),
        ]);
        _downloader.Serve(
            "https://github.com/x/GE-Proton11-6-aarch64.tar.gz",
            BuildProtonArchive("GE-Proton11-6-aarch64", wineserverElfMachine: ElfAarch64));

        var path = await NewProvisioner(Architecture.Arm64).EnsureProtonAsync("GE-Proton");

        Assert.EndsWith(
            NormalizeSeparators(Path.Combine("compatibilitytools.d", "GE-Proton11-6")),
            NormalizeSeparators(path));
        Assert.Equal(["https://github.com/x/GE-Proton11-6-aarch64.tar.gz"], _downloader.Requests);
    }

    [Fact]
    public async Task EnsureProtonAsync_WineserverArchMismatch_AbortsAndCleans()
    {
        // 无后缀资产名骗过名称过滤时，wineserver 的 ELF e_machine 兜底拦截
        ServeGitHubRelease(UmuComponentProvisioner.GeProtonReleaseApi, "GE-Proton11-6", [
            ("GE-Proton11-6.tar.gz", "https://github.com/x/GE-Proton11-6.tar.gz"),
        ]);
        _downloader.Serve(
            "https://github.com/x/GE-Proton11-6.tar.gz",
            BuildProtonArchive("GE-Proton11-6", wineserverElfMachine: ElfAarch64));

        var ex = await Assert.ThrowsAsync<LaunchException>(
            () => NewProvisioner(Architecture.X64).EnsureProtonAsync("GE-Proton"));

        Assert.Equal(LaunchFailureKind.ProtonDownloadFailed, ex.Kind);
        Assert.Contains("架构", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(
            Path.Combine(UmuPaths.SteamCompatRoot(_tempDir.Path), "GE-Proton11-6"))); // 已清理
    }

    [Fact]
    public async Task EnsureProtonAsync_WineserverArchMatches_Installs()
    {
        ServeGitHubRelease(UmuComponentProvisioner.GeProtonReleaseApi, "GE-Proton11-6", [
            ("GE-Proton11-6.tar.gz", "https://github.com/x/GE-Proton11-6.tar.gz"),
        ]);
        _downloader.Serve(
            "https://github.com/x/GE-Proton11-6.tar.gz",
            BuildProtonArchive("GE-Proton11-6", wineserverElfMachine: ElfX86_64));

        var path = await NewProvisioner(Architecture.X64).EnsureProtonAsync("GE-Proton");

        Assert.True(_provisioner.IsProtonReady(path));
    }

    [Fact]
    public void FindInstalledProton_WrongArchLocal_Skipped_CorrectArch_Found()
    {
        // 错架构已装目录视同缺失（自愈前提）；对架构目录照常解析
        var wrong = InstallReadyProton("GE-Proton11-6");
        WriteWineserverElf(wrong, ElfAarch64);

        var arm = NewProvisioner(Architecture.Arm64);
        var x64 = NewProvisioner(Architecture.X64);
        Assert.Null(x64.FindInstalledProton("GE-Proton")); // 代号前缀路径
        Assert.Null(x64.FindInstalledProton("GE-Proton11-6")); // 版本名路径
        Assert.NotNull(arm.FindInstalledProton("GE-Proton11-6")); // aarch64 主机上同目录可用

        WriteWineserverElf(wrong, ElfX86_64);
        Assert.NotNull(x64.FindInstalledProton("GE-Proton")); // 换成对架构后恢复可解析
    }

    [Fact]
    public async Task EnsureProtonAsync_PreinstalledWrongArch_SelfHealsByRedownload()
    {
        // 事故自愈路径：本地已装 aarch64 GE-Proton11-6（x86_64 主机）→ 视同缺失 →
        // 同版本重下（ExtractSingleTopLevel 原地替换）→ wineserver 变为本机架构
        var wrong = InstallReadyProton("GE-Proton11-6");
        WriteWineserverElf(wrong, ElfAarch64);
        ServeGitHubRelease(UmuComponentProvisioner.GeProtonReleaseApi, "GE-Proton11-6", [
            ("GE-Proton11-6-x86_64.tar.gz", "https://github.com/x/GE-Proton11-6-x86_64.tar.gz"),
        ]);
        _downloader.Serve(
            "https://github.com/x/GE-Proton11-6-x86_64.tar.gz",
            BuildProtonArchive("GE-Proton11-6-x86_64", wineserverElfMachine: ElfX86_64));

        var path = await NewProvisioner(Architecture.X64).EnsureProtonAsync("GE-Proton");

        Assert.EndsWith(
            NormalizeSeparators(Path.Combine("compatibilitytools.d", "GE-Proton11-6")),
            NormalizeSeparators(path));
        Assert.Equal(["https://github.com/x/GE-Proton11-6-x86_64.tar.gz"], _downloader.Requests);
        Assert.Equal(ElfX86_64, UmuComponentProvisioner.ReadWineserverElfMachine(path)); // 已替换为本机架构
    }

    [Fact]
    public async Task FetchLatestProtonTagAsync_ResolvesCodenameToUpstreamTag()
    {
        ServeGitHubRelease(UmuComponentProvisioner.GeProtonReleaseApi, "GE-Proton11-7", []);
        ServeForgejoReleases([]); // Forgejo 数组根同样能取 tag
        ServeGitHubRelease(UmuComponentProvisioner.UmuProtonReleaseApi, "UMU-Proton-10.0-9", []);

        Assert.Equal("GE-Proton11-7", await _provisioner.FetchLatestProtonTagAsync("GE-Proton"));
        Assert.Equal("dwproton-11.0-99", await _provisioner.FetchLatestProtonTagAsync("DW-Proton"));
        Assert.Equal("UMU-Proton-10.0-9", await _provisioner.FetchLatestProtonTagAsync("UMU-Proton"));
    }

    [Fact]
    public async Task FetchLatestProtonTagAsync_UnsupportedCodename_Throws()
    {
        var ex = await Assert.ThrowsAsync<LaunchException>(
            () => _provisioner.FetchLatestProtonTagAsync("XX-Proton"));

        Assert.Equal(LaunchFailureKind.ProtonDownloadFailed, ex.Kind);
    }

    [Fact]
    public async Task EnsureRuntimeAsync_CatalogFetchFails_ClassifiesAsUmuRuntimeDownloadFailed()
    {
        // 回归：runtime 版本号/SHA256SUMS/BUILD_ID 拉取失败曾以裸 HttpRequestException 逃逸，
        // 在 VM 落 Unknown 丢重试修复 UI；应与包本体下载同归类为 UmuRuntimeDownloadFailed
        var ex = await Assert.ThrowsAsync<LaunchException>(
            () => _provisioner.EnsureRuntimeAsync("sniper", "SteamLinuxRuntime_sniper"));

        Assert.Equal(LaunchFailureKind.UmuRuntimeDownloadFailed, ex.Kind);
    }

    [Fact]
    public async Task UpdateProtonAsync_InstallsLatestAndPrunesSameFlavorOldVersions()
    {
        InstallReadyProton("GE-Proton10-9");
        InstallReadyProton("UMU-Proton-10.0-1"); // 其它发行版不受影响
        ServeGitHubRelease(UmuComponentProvisioner.GeProtonReleaseApi, "GE-Proton11-6", [
            ("GE-Proton11-6-x86_64.tar.gz", "https://github.com/x/GE-Proton11-6-x86_64.tar.gz"),
        ]);
        _downloader.Serve(
            "https://github.com/x/GE-Proton11-6-x86_64.tar.gz",
            BuildProtonArchive("GE-Proton11-6-x86_64", wineserverElfMachine: ElfX86_64));

        var path = await NewProvisioner(Architecture.X64).UpdateProtonAsync("GE-Proton");

        var root = UmuPaths.SteamCompatRoot(_tempDir.Path);
        Assert.EndsWith(
            NormalizeSeparators(Path.Combine("compatibilitytools.d", "GE-Proton11-6")),
            NormalizeSeparators(path));
        Assert.False(Directory.Exists(Path.Combine(root, "GE-Proton10-9"))); // 同发行版旧版已删
        Assert.True(Directory.Exists(Path.Combine(root, "UMU-Proton-10.0-1"))); // 其它发行版保留
    }

    [Fact]
    public async Task UpdateProtonAsync_AlreadyLatest_ReturnsExistingWithoutDownload()
    {
        InstallReadyProton("GE-Proton11-6");
        ServeGitHubRelease(UmuComponentProvisioner.GeProtonReleaseApi, "GE-Proton11-6", [
            ("GE-Proton11-6.tar.gz", "https://github.com/x/GE-Proton11-6.tar.gz"),
        ]);

        var path = await _provisioner.UpdateProtonAsync("GE-Proton");

        Assert.EndsWith(
            NormalizeSeparators(Path.Combine("compatibilitytools.d", "GE-Proton11-6")),
            NormalizeSeparators(path));
        Assert.Empty(_downloader.Requests);
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

    // ==== 2026-09-22 测试审计补齐：按 tag 下载、默认代号回退、latest 解析容错 ====
    // （覆盖率证据：DownloadProtonByTagAsync 此前整方法未覆盖，空请求/畸形 release 分支同批缺口）

    [Fact]
    public async Task EnsureProtonAsync_SpecificVersionName_DownloadsThatTagOnly()
    {
        // 请求是具体版本名（非代号、本地未装）：只下该 tag 的 release，不拉 latest
        ServeGitHubRelease(
            "https://api.github.com/repos/GloriousEggroll/proton-ge-custom/releases/tags/GE-Proton11-6",
            "GE-Proton11-6",
            [("GE-Proton11-6.tar.gz", "https://github.com/x/GE-Proton11-6.tar.gz")]);
        _downloader.Serve(
            "https://github.com/x/GE-Proton11-6.tar.gz",
            BuildProtonArchive("GE-Proton11-6", wineserverElfMachine: ElfX86_64));

        var path = await NewProvisioner(Architecture.X64).EnsureProtonAsync("GE-Proton11-6");

        Assert.EndsWith(
            NormalizeSeparators(Path.Combine("compatibilitytools.d", "GE-Proton11-6")),
            NormalizeSeparators(path));
        Assert.Equal(["https://github.com/x/GE-Proton11-6.tar.gz"], _downloader.Requests);
    }

    [Fact]
    public async Task EnsureProtonAsync_UnknownVersionTag404_ThrowsWithCodenameHint()
    {
        // 404 是"版本不存在"的用户可修复错误：提示改用代号或本机已装版本
        var ex = await Assert.ThrowsAsync<LaunchException>(
            () => NewProvisioner(Architecture.X64).EnsureProtonAsync("GE-Proton99-0"));

        Assert.Equal(LaunchFailureKind.ProtonDownloadFailed, ex.Kind);
        Assert.Contains("找不到 Proton 版本", ex.Message, StringComparison.Ordinal);
        Assert.Contains("DW-Proton", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_downloader.Requests);
    }

    [Fact]
    public async Task EnsureProtonAsync_EmptyRequest_FallsBackToDefaultFlavor()
    {
        // 空请求 = CompatTools.DefaultProtonFlavor（DW-Proton）：latest 走 Forgejo
        ServeForgejoReleases([
            ("dwproton-11.0-99-x86_64.tar.xz", "https://dawn.wine/x/dwproton-11.0-99-x86_64.tar.xz"),
        ]);
        _downloader.Serve(
            "https://dawn.wine/x/dwproton-11.0-99-x86_64.tar.xz",
            BuildProtonArchive("dwproton-11.0-99-x86_64", wineserverElfMachine: ElfX86_64));

        var path = await NewProvisioner(Architecture.X64).EnsureProtonAsync("");

        Assert.EndsWith(
            NormalizeSeparators(Path.Combine("compatibilitytools.d", "dwproton-11.0-99")),
            NormalizeSeparators(path));
    }

    [Fact]
    public async Task FetchLatestProtonTagAsync_EmptyRequest_UsesDefaultFlavor()
    {
        ServeForgejoReleases([]);

        Assert.Equal("dwproton-11.0-99", await _provisioner.FetchLatestProtonTagAsync(""));
    }

    [Fact]
    public async Task FetchLatestProtonTagAsync_DraftAndPrereleaseReleases_Skipped()
    {
        // Forgejo latest 数组：draft/prerelease 不算最新（社区源常把草稿排前）
        _http.Map(UmuComponentProvisioner.DwProtonReleaseApi, """
            [
              { "tag_name": "draft-1", "draft": true, "prerelease": false, "assets": [] },
              { "tag_name": "pre-1", "draft": false, "prerelease": true, "assets": [] },
              { "tag_name": "dwproton-11.0-99", "draft": false, "prerelease": false, "assets": [] }
            ]
            """);

        Assert.Equal("dwproton-11.0-99", await _provisioner.FetchLatestProtonTagAsync("DW-Proton"));
    }

    [Fact]
    public async Task FetchLatestProtonTagAsync_AllReleasesFilteredOut_ThrowsMissingTag()
    {
        _http.Map(UmuComponentProvisioner.DwProtonReleaseApi,
            """[ { "tag_name": "x", "draft": true, "prerelease": false, "assets": [] } ]""");

        var ex = await Assert.ThrowsAsync<LaunchException>(
            () => _provisioner.FetchLatestProtonTagAsync("DW-Proton"));

        Assert.Equal(LaunchFailureKind.ProtonDownloadFailed, ex.Kind);
        Assert.Contains("tag_name", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchLatestProtonTagAsync_InvalidJson_ThrowsParseFailure()
    {
        _http.Map(UmuComponentProvisioner.DwProtonReleaseApi, "{ not json");

        var ex = await Assert.ThrowsAsync<LaunchException>(
            () => _provisioner.FetchLatestProtonTagAsync("DW-Proton"));

        Assert.Equal(LaunchFailureKind.ProtonDownloadFailed, ex.Kind);
        Assert.Contains("解析失败", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadWineserverElfMachine_ShortOrNonElfFile_ReturnsNull()
    {
        using var tempDir = new TempDir();
        var bin = Path.Combine(tempDir.Path, "files", "bin");
        Directory.CreateDirectory(bin);
        var wineserver = Path.Combine(bin, "wineserver");

        // 过短（不足 20 字节 ELF 头）
        File.WriteAllBytes(wineserver, "hello"u8.ToArray());
        Assert.Null(UmuComponentProvisioner.ReadWineserverElfMachine(tempDir.Path));

        // 足长但非 ELF 魔数
        File.WriteAllBytes(wineserver, new byte[20]);
        Assert.Null(UmuComponentProvisioner.ReadWineserverElfMachine(tempDir.Path));
    }

    /// <summary>路径断言两侧统一成正斜杠：Windows 上 Path.Combine 产反斜杠，不归一则永不相等。</summary>
    private static string NormalizeSeparators(string path) => path.Replace('\\', '/');

    /// <summary>在 compatibilitytools.d 下放一个"就绪"的 Proton 目录（toolmanifest.vdf + proton），返回目录路径。</summary>
    private string InstallReadyProton(string name)
    {
        var dir = Path.Combine(UmuPaths.SteamCompatRoot(_tempDir.Path), name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "toolmanifest.vdf"), "\"manifest\" { }");
        File.WriteAllText(Path.Combine(dir, "proton"), "#!/bin/sh\n");
        return dir;
    }

    /// <summary>向已装 Proton 目录写入指定 e_machine 的 wineserver ELF 头（本地架构过滤测试用）。</summary>
    private static void WriteWineserverElf(string protonDir, ushort machine)
    {
        var bin = Path.Combine(protonDir, "files", "bin");
        Directory.CreateDirectory(bin);
        var elf = new byte[20];
        elf[0] = 0x7F;
        elf[1] = (byte)'E';
        elf[2] = (byte)'L';
        elf[3] = (byte)'F';
        elf[4] = 2;
        elf[18] = (byte)(machine & 0xFF);
        elf[19] = (byte)(machine >> 8);
        File.WriteAllBytes(Path.Combine(bin, "wineserver"), elf);
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
    /// wineserverElfMachine 非空时附加 files/bin/wineserver（20 字节 ELF 头，e_machine 为给定值），
    /// 供架构兜底校验测试。解压按魔数而非扩展名分发，故 DW 的 .tar.xz 资产名配 gzip 内容即可
    /// （SharpCompress 无 XZ 编码器，与 UmuArchiveExtractionTests 同一取舍）。
    /// </summary>
    private static byte[] BuildProtonArchive(string topDir, ushort? wineserverElfMachine = null)
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
            if (wineserverElfMachine is { } machine)
            {
                var elf = new byte[20];
                elf[0] = 0x7F;
                elf[1] = (byte)'E';
                elf[2] = (byte)'L';
                elf[3] = (byte)'F';
                elf[4] = 2; // 64 位
                elf[18] = (byte)(machine & 0xFF);
                elf[19] = (byte)(machine >> 8);
                var wineserver = new PaxTarEntry(TarEntryType.RegularFile, $"{topDir}/files/bin/wineserver")
                {
                    DataStream = new MemoryStream(elf),
                };
                writer.WriteEntry(wineserver);
            }
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

public class UmuRuntimeTimeoutClassificationTests
{
    /// <summary>回归（2026-09-20 三审）：Runtime 版本号直连拉取的超时（token 未取消的 TCE）
    /// 曾裸 OCE 上抛——设置页取消豁免静默吞、启动路径落 Unknown，与 Proton 下载超时分类不一致。</summary>
    [Fact]
    public async Task EnsureRuntimeAsync_DirectFetchTimeout_ClassifiesAsUmuRuntimeDownloadFailed()
    {
        var handler = new StubHttpHandler { TimeoutFirstN = 1 };
        using var tempDir = new TempDir();
        var provisioner = new UmuComponentProvisioner(
            new HttpClient(handler), new FakeDownloader(), dataHome: tempDir.Path, cacheHome: tempDir.Path);

        var ex = await Assert.ThrowsAsync<LaunchException>(
            () => provisioner.EnsureRuntimeAsync("sniper", "SteamLinuxRuntime_sniper"));

        Assert.Equal(LaunchFailureKind.UmuRuntimeDownloadFailed, ex.Kind);
    }

    /// <summary>回归（2026-09-20 三审）：已装 Proton 声明 host（无 require_tool_appid）是已知事实，
    /// 不得返回 null（null = 未知 → 设置页按 steamrt4 准备，与启动路径免容器直跑矛盾）。</summary>
    [Fact]
    public void ResolveRequiredRuntime_HostManifest_ReturnsHostFactInsteadOfNull()
    {
        using var tempDir = new TempDir();
        var provisioner = new UmuComponentProvisioner(
            new HttpClient(new StubHttpHandler()), new FakeDownloader(), dataHome: tempDir.Path, cacheHome: tempDir.Path);
        var dir = Path.Combine(UmuPaths.SteamCompatRoot(tempDir.Path), "GE-Proton10-9");
        Directory.CreateDirectory(dir);
        // commandline 必填：缺失时 ToolManifest.Load 抛 UpdateException，走进"清单不可读"null 分支
        File.WriteAllText(
            Path.Combine(dir, "toolmanifest.vdf"),
            "\"manifest\" { \"commandline\" \"/proton %verb%\" }");
        File.WriteAllText(Path.Combine(dir, "proton"), "#!/bin/sh\n");
        // 架构过滤按 wineserver ELF 判定——缺它视同未安装
        var bin = Path.Combine(dir, "files", "bin");
        Directory.CreateDirectory(bin);
        var elf = new byte[20];
        elf[0] = 0x7F;
        elf[1] = (byte)'E';
        elf[2] = (byte)'L';
        elf[3] = (byte)'F';
        elf[18] = 0x3E; // e_machine = EM_X86_64
        File.WriteAllBytes(Path.Combine(bin, "wineserver"), elf);

        var result = provisioner.ResolveRequiredRuntime("GE-Proton10-9");

        Assert.NotNull(result);
        Assert.Equal("", result.Value.Variant);
        Assert.Equal("host", result.Value.Name);
    }
}
