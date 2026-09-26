using System.Text;
using Xunit;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Dependencies;
using YetAnotherGameLauncher.Core.Utilities;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Core.Tests.Dependencies;

/// <summary>
/// 依赖安装器五阶段编排：下载→解压→字体落位→注册表导入→完成标记。
/// 断言覆盖顺序副作用（缓存/标记/临时 .reg 清理）与全部失败分类。
/// </summary>
public class DependencyInstallerTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly FakeDownloader _downloader = new();
    private readonly FakeProcessRunner _runner = new();
    private readonly byte[] _archiveBytes;

    public DependencyInstallerTests()
    {
        _archiveBytes = TestZip.Create(
            ("a.ttc", "font A"u8.ToArray()),
            ("b.ttf", "font B"u8.ToArray()));
    }

    /// <summary>prefix 根（WINEPREFIX 指向处，含 drive_c 视为已初始化）；状态目录按生产布局挂其内。</summary>
    private string Prefix => _temp.FilePath("prefix");

    private string StateDir => _temp.FilePath("prefix", ".yagl-deps");

    private string CacheRoot => _temp.FilePath("cache");

    /// <summary>解压暂存目录（cacheRoot/staging/test-fonts，"用后即清"守卫的探针位置）。</summary>
    private string StagingDir => _temp.FilePath("cache", "staging", "test-fonts");

    /// <summary>两字体清单；md5/size 与 _archiveBytes 一致（缓存有效性判定要用真值）。</summary>
    private DependencyManifest BuildManifest() => new(
        "test-fonts", "1.0",
        "https://example.com/fonts.zip", "fonts.zip",
        Hashing.Md5Hex(_archiveBytes), _archiveBytes.Length,
        [new DependencyFont("a.ttc", ["F1", "F2"]), new DependencyFont("b.ttf", ["F3"])],
        [new DependencyReplacementGroup("F1", ["OldFont"])]);

    private DependencyInstaller CreateInstaller() =>
        new(_downloader, _runner, [BuildManifest()], cacheRoot: CacheRoot);

    private WinePrefixTarget MakeTarget() => new(
        CreateWineExecutable(),
        Prefix,
        Path.Combine(Prefix, "drive_c", "windows", "Fonts"),
        StateDir);

    /// <summary>造一个"可执行"的 wine 替身文件（Linux 补执行位，Windows 存在即可）。</summary>
    private string CreateWineExecutable()
    {
        var path = _temp.FilePath("wine-bin", "wine");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "#!/bin/sh\n");
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    /// <summary>建已初始化 prefix（drive_c 存在）。</summary>
    private void InitPrefix() => Directory.CreateDirectory(Path.Combine(Prefix, "drive_c", "windows"));

    /// <summary>从 wine 命令参数里提取 .reg 路径（"reg import \"path\"" 形态）。</summary>
    private static string ExtractRegPath(ProcessStartSpec spec)
    {
        const string marker = "reg import \"";
        var start = spec.Arguments.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = spec.Arguments.IndexOf('"', start);
        return spec.Arguments[start..end];
    }

    /// <summary>同步进度收集（Progress&lt;T&gt; 异步投递不保证时序，测试禁用）。</summary>
    private sealed class CollectProgress : IProgress<DependencyProgress>
    {
        public List<DependencyProgress> Items { get; } = [];
        public void Report(DependencyProgress value) => Items.Add(value);
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task Install_HappyPath_CopiesFontsImportsRegistryWritesMarker()
    {
        InitPrefix();
        _downloader.Serve("https://example.com/fonts.zip", _archiveBytes);
        byte[]? regBytes = null;
        _runner.Handler = spec =>
        {
            regBytes = File.ReadAllBytes(ExtractRegPath(spec));
            return new ProcessResult(0, "", "");
        };

        var progress = new CollectProgress();
        await CreateInstaller().InstallAsync(MakeTarget(), BuildManifest(), progress);

        // 字体落位：两文件进 windows/Fonts，内容与压缩包一致
        Assert.Equal("font A", await File.ReadAllTextAsync(Path.Combine(Prefix, "drive_c", "windows", "Fonts", "a.ttc")));
        Assert.Equal("font B", await File.ReadAllTextAsync(Path.Combine(Prefix, "drive_c", "windows", "Fonts", "b.ttf")));

        // 注册表导入：一次 wine 调用；WINEPREFIX 指向 prefix；防 mono/gecko 弹窗覆盖
        var spec = Assert.Single(_runner.Specs);
        Assert.Equal("wine", Path.GetFileName(spec.FileName));
        Assert.StartsWith("reg import \"", spec.Arguments);
        Assert.Equal(Prefix, spec.Environment!["WINEPREFIX"]);
        Assert.Equal("mscoree,mshtml=", spec.Environment["WINEDLLOVERRIDES"]);

        // .reg 内容：UTF-16LE BOM + 两个节
        Assert.NotNull(regBytes);
        Assert.Equal(0xFF, regBytes[0]);
        Assert.Equal(0xFE, regBytes[1]);
        var regText = Encoding.Unicode.GetString(regBytes[2..]);
        Assert.Contains("[HKEY_LOCAL_MACHINE\\Software\\Microsoft\\Windows NT\\CurrentVersion\\Fonts]", regText);
        Assert.Contains("\"F1\"=\"a.ttc\"", regText);
        Assert.Contains("[HKEY_CURRENT_USER\\Software\\Wine\\Fonts\\Replacements]", regText);
        Assert.Contains("\"OldFont\"=\"F1\"", regText);

        // 临时 .reg 用后即清
        Assert.Empty(Directory.GetFiles(StateDir, "*.reg*"));

        // 暂存"用后即清"守卫（变异击杀位：删掉清理 finally 本用例必红）
        Assert.False(Directory.Exists(StagingDir), "解压暂存目录未清理");

        // 完成标记最后写：id@version
        Assert.Equal("test-fonts@1.0", await File.ReadAllTextAsync(Path.Combine(StateDir, "test-fonts.ok")));

        // 进度以 Done 收尾
        Assert.Equal(DependencyPhase.Done, progress.Items[^1].Phase);
    }

    [Fact]
    public async Task Install_ValidCachedArchive_SkipsDownload()
    {
        InitPrefix();
        Directory.CreateDirectory(CacheRoot);
        await File.WriteAllBytesAsync(Path.Combine(CacheRoot, "fonts.zip"), _archiveBytes);

        await CreateInstaller().InstallAsync(MakeTarget(), BuildManifest(), null);

        Assert.Empty(_downloader.Requests);
        Assert.Single(_runner.Specs);
    }

    [Fact]
    public async Task Install_CachedArchiveCorrupt_Redownloads()
    {
        InitPrefix();
        Directory.CreateDirectory(CacheRoot);
        await File.WriteAllBytesAsync(Path.Combine(CacheRoot, "fonts.zip"), [0x00, 0x01]);
        _downloader.Serve("https://example.com/fonts.zip", _archiveBytes);

        await CreateInstaller().InstallAsync(MakeTarget(), BuildManifest(), null);

        Assert.Single(_downloader.Requests);
    }

    [Fact]
    public async Task Install_DownloadFails_ThrowsDownloadFailed_NoMarker()
    {
        InitPrefix();
        _downloader.FailUrls.Add("https://example.com/fonts.zip");

        var ex = await Assert.ThrowsAsync<DependencyException>(
            () => CreateInstaller().InstallAsync(MakeTarget(), BuildManifest(), null));

        Assert.Equal(DependencyFailureKind.DownloadFailed, ex.Kind);
        Assert.False(File.Exists(Path.Combine(StateDir, "test-fonts.ok")));
    }

    [Fact]
    public async Task Install_FontFileMissing_ThrowsExtractFailed_NoStagingLeft()
    {
        InitPrefix();
        // 压缩包缺少清单声明的 b.ttf：解压成功但字体校验失败，暂存必须照样清理
        _downloader.Serve(
            "https://example.com/fonts.zip",
            TestZip.Create(("a.ttc", "font A"u8.ToArray())));

        var ex = await Assert.ThrowsAsync<DependencyException>(
            () => CreateInstaller().InstallAsync(MakeTarget(), BuildManifest(), null));

        Assert.Equal(DependencyFailureKind.ExtractFailed, ex.Kind);
        Assert.False(Directory.Exists(StagingDir), "字体条目缺失时解压暂存目录未清理");
    }

    [Fact]
    public async Task Install_WineMissing_ThrowsWineMissing_BeforeAnyDownload()
    {
        InitPrefix();
        var target = new WinePrefixTarget(
            _temp.FilePath("no-such-wine"), Prefix,
            Path.Combine(Prefix, "drive_c", "windows", "Fonts"), StateDir);

        var ex = await Assert.ThrowsAsync<DependencyException>(
            () => CreateInstaller().InstallAsync(target, BuildManifest(), null));

        Assert.Equal(DependencyFailureKind.WineMissing, ex.Kind);
        Assert.Empty(_downloader.Requests);
        Assert.Empty(_runner.Specs);
    }

    [Fact]
    public async Task Install_PrefixNotInitialized_ThrowsPrefixMissing_BeforeAnyDownload()
    {
        // prefix 目录不存在（首启前）：明确要求先启动一次游戏，而不是悄悄创建半成品 prefix
        var target = new WinePrefixTarget(
            CreateWineExecutable(), Prefix,
            Path.Combine(Prefix, "drive_c", "windows", "Fonts"), StateDir);

        var ex = await Assert.ThrowsAsync<DependencyException>(
            () => CreateInstaller().InstallAsync(target, BuildManifest(), null));

        Assert.Equal(DependencyFailureKind.PrefixMissing, ex.Kind);
        Assert.Empty(_downloader.Requests);
    }

    [Fact]
    public async Task Install_RegistryFails_ThrowsRegistryFailed_NoMarker_NoRegLeftover()
    {
        InitPrefix();
        _downloader.Serve("https://example.com/fonts.zip", _archiveBytes);
        _runner.Handler = _ => new ProcessResult(1, "", "wineserver exploded");

        var ex = await Assert.ThrowsAsync<DependencyException>(
            () => CreateInstaller().InstallAsync(MakeTarget(), BuildManifest(), null));

        Assert.Equal(DependencyFailureKind.RegistryFailed, ex.Kind);
        Assert.Contains("wineserver exploded", ex.Message);
        Assert.False(File.Exists(Path.Combine(StateDir, "test-fonts.ok")));
        Assert.Empty(Directory.GetFiles(StateDir, "*.reg*"));
        Assert.False(Directory.Exists(StagingDir), "注册表失败时解压暂存目录未清理");
    }

    /// <summary>注册表阶段的脚本写盘失败（状态目录被文件占位 → 建目录抛 IOException）同样按
    /// RegistryFailed 分类，不得裸穿。</summary>
    [Fact]
    public async Task Install_RegistryScriptWriteFails_ThrowsRegistryFailed()
    {
        InitPrefix();
        _downloader.Serve("https://example.com/fonts.zip", _archiveBytes);
        Directory.CreateDirectory(Path.GetDirectoryName(StateDir)!);
        File.WriteAllText(StateDir, "占位文件：状态目录路径被文件占用");

        var ex = await Assert.ThrowsAsync<DependencyException>(
            () => CreateInstaller().InstallAsync(MakeTarget(), BuildManifest(), null));

        Assert.Equal(DependencyFailureKind.RegistryFailed, ex.Kind);
        Assert.False(File.Exists(Path.Combine(StateDir, "test-fonts.ok")));
    }

    /// <summary>注册表成功但完成标记写不进（标记路径被目录占位）：按 StateWriteFailed 分类，
    /// 不得裸穿 IOException——此时字体已生效但状态未记录，用户必须得到失败提示。</summary>
    [Fact]
    public async Task Install_MarkerWriteFails_ThrowsStateWriteFailed()
    {
        InitPrefix();
        _downloader.Serve("https://example.com/fonts.zip", _archiveBytes);
        Directory.CreateDirectory(Path.Combine(StateDir, "test-fonts.ok")); // 标记路径被目录占位

        var ex = await Assert.ThrowsAsync<DependencyException>(
            () => CreateInstaller().InstallAsync(MakeTarget(), BuildManifest(), null));

        Assert.Equal(DependencyFailureKind.StateWriteFailed, ex.Kind);
    }

    /// <summary>reg import 执行阶段抛非 OCE（生产 SystemProcessRunner 超时抛 UpdateException）：
    /// 按 RegistryFailed 分类而非裸穿。</summary>
    [Fact]
    public async Task Install_RegistryRunnerThrows_ThrowsRegistryFailed()
    {
        InitPrefix();
        _downloader.Serve("https://example.com/fonts.zip", _archiveBytes);
        _runner.Handler = _ => throw new UpdateException("超时");

        var ex = await Assert.ThrowsAsync<DependencyException>(
            () => CreateInstaller().InstallAsync(MakeTarget(), BuildManifest(), null));

        Assert.Equal(DependencyFailureKind.RegistryFailed, ex.Kind);
        Assert.Contains("超时", ex.Message);
    }

    /// <summary>同尺寸但内容不同的缓存必须判无效走重下（MD5 分支；尺寸分支已有覆盖）。</summary>
    [Fact]
    public async Task Install_CachedArchiveSameSizeWrongContent_Redownloads()
    {
        InitPrefix();
        Directory.CreateDirectory(CacheRoot);
        var sameSizeWrongContent = new byte[_archiveBytes.Length];
        Array.Fill(sameSizeWrongContent, (byte)0xAB);
        await File.WriteAllBytesAsync(Path.Combine(CacheRoot, "fonts.zip"), sameSizeWrongContent);
        _downloader.Serve("https://example.com/fonts.zip", _archiveBytes);

        await CreateInstaller().InstallAsync(MakeTarget(), BuildManifest(), null);

        Assert.Single(_downloader.Requests);
    }

    /// <summary>并发防护：同依赖的两个安装调用串行执行，后者凭先者写入的标记短路，
    /// 注册表导入只发生一次（跨 VM/跨游戏并发互踩的回归守卫）。</summary>
    [Fact]
    public async Task Install_ConcurrentCalls_SerializedAndSecondShortCircuits()
    {
        InitPrefix();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gatedDownloader = new GatedDownloader(gate.Task, _archiveBytes);
        var installer = new DependencyInstaller(gatedDownloader, _runner, [BuildManifest()], cacheRoot: CacheRoot);
        var target = MakeTarget();
        var manifest = BuildManifest();

        var first = installer.InstallAsync(target, manifest);
        var secondTask = installer.InstallAsync(target, manifest); // 第一枪挂在下载门上时发起
        Assert.False(first.IsCompleted);

        await Task.Yield();
        gate.TrySetResult();
        await Task.WhenAll(first, secondTask);

        var import = Assert.Single(_runner.Specs);
        Assert.StartsWith("reg import \"", import.Arguments);
        Assert.Equal("test-fonts@1.0", await File.ReadAllTextAsync(Path.Combine(StateDir, "test-fonts.ok")));
    }

    /// <summary>挂在可释放门上的下载器替身（并发串行化测试用）。</summary>
    private sealed class GatedDownloader(Task gate, byte[] content) : IDownloader
    {
        public async Task DownloadFileAsync(
            Core.Models.DownloadRequest request,
            IProgress<long>? progress = null,
            CancellationToken cancellationToken = default)
        {
            await gate;
            Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
            await File.WriteAllBytesAsync(request.DestinationPath, content, cancellationToken);
        }
    }

    [Fact]
    public async Task Install_AlreadyInstalled_ShortCircuits()
    {
        InitPrefix();
        Directory.CreateDirectory(StateDir);
        await File.WriteAllTextAsync(Path.Combine(StateDir, "test-fonts.ok"), "test-fonts@1.0");

        await CreateInstaller().InstallAsync(MakeTarget(), BuildManifest(), null);

        Assert.Empty(_downloader.Requests);
        Assert.Empty(_runner.Specs);
    }

    [Fact]
    public async Task Install_VersionChanged_Reinstalls()
    {
        InitPrefix();
        Directory.CreateDirectory(StateDir);
        await File.WriteAllTextAsync(Path.Combine(StateDir, "test-fonts.ok"), "test-fonts@0.9");
        _downloader.Serve("https://example.com/fonts.zip", _archiveBytes);

        await CreateInstaller().InstallAsync(MakeTarget(), BuildManifest(), null);

        Assert.Single(_runner.Specs);
        Assert.Equal("test-fonts@1.0", await File.ReadAllTextAsync(Path.Combine(StateDir, "test-fonts.ok")));
    }

    [Fact]
    public async Task Install_Cancelled_PropagatesCancellation()
    {
        InitPrefix();
        var installer = new DependencyInstaller(
            new CancelledDownloader(), _runner, [BuildManifest()], cacheRoot: CacheRoot);

        // 取消可来自任一阶段（信号量等待 / 下载器）：契约 = 以 OCE 族透传，不折算成业务失败
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => installer.InstallAsync(MakeTarget(), BuildManifest(), null, new CancellationToken(canceled: true)));
    }

    [Fact]
    public void GetState_ReflectsMarkerContent()
    {
        var target = MakeTarget();
        var installer = CreateInstaller();

        Assert.False(installer.GetState(target, BuildManifest()).Installed);

        Directory.CreateDirectory(StateDir);
        File.WriteAllText(Path.Combine(StateDir, "test-fonts.ok"), "test-fonts@1.0");
        var state = installer.GetState(target, BuildManifest());
        Assert.True(state.Installed);
        Assert.Equal("1.0", state.InstalledVersion);

        // prefix 重置（标记被删）→ 回到未安装
        Directory.Delete(StateDir, recursive: true);
        Assert.False(installer.GetState(target, BuildManifest()).Installed);
    }

    /// <summary>构造函数缺省依赖清单 = 内嵌目录（装配自检）。</summary>
    [Fact]
    public void Constructor_WithoutDependencies_LoadsEmbeddedCatalog()
    {
        var installer = new DependencyInstaller(_downloader, _runner, cacheRoot: CacheRoot);
        Assert.Contains(installer.Dependencies, d => d.Id == "cjk-fonts");
    }

    private sealed class CancelledDownloader : IDownloader
    {
        public Task DownloadFileAsync(
            Core.Models.DownloadRequest request,
            IProgress<long>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
