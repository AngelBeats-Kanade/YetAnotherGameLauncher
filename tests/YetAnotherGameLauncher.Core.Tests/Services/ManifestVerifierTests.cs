using Xunit;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Core.Utilities;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Core.Tests.Services;

public class ManifestVerifierTests : IDisposable
{
    private readonly TempDir _tempDir = new();

    public void Dispose() => _tempDir.Dispose();

    private static string Md5(byte[] data) => Hashing.Md5Hex(data);

    private static ManifestFile FileEntry(string path, byte[] content) =>
        new(path, content.Length, Md5(content));

    private static void WriteFile(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    [Fact]
    public void Verify_ReportsPerFileProgress()
    {
        // 大库全量 MD5 耗时数分钟：逐文件回调驱动进度条，防止"进度卡 0%"误判为卡死
        var a = "hello"u8.ToArray();
        var b = "world!!"u8.ToArray();
        WriteFile(_tempDir.FilePath("a.txt"), a);
        WriteFile(_tempDir.FilePath("sub", "b.txt"), b);
        var manifest = new GameManifest
        {
            Version = "1.0.0",
            Files = [FileEntry("a.txt", a), FileEntry("sub/b.txt", b)],
        };
        var seen = new List<(int Checked, int Total)>();

        var result = ManifestVerifier.VerifyFull(_tempDir.Path, manifest, (c, t) => seen.Add((c, t)));

        Assert.True(result.IsComplete);
        Assert.Equal([(1, 2), (2, 2)], seen);
    }

    [Fact]
    public void VerifyFast_AllFilesOk()
    {
        var a = "hello"u8.ToArray();
        var b = "world!!"u8.ToArray();
        WriteFile(_tempDir.FilePath("a.txt"), a);
        WriteFile(_tempDir.FilePath("sub", "b.txt"), b);
        var manifest = new GameManifest
        {
            Version = "1.0.0",
            Files = [FileEntry("a.txt", a), FileEntry("sub/b.txt", b)],
        };

        var result = ManifestVerifier.VerifyFast(_tempDir.Path, manifest);

        Assert.True(result.IsComplete);
        Assert.Empty(result.NeedsDownload);
    }

    [Fact]
    public void VerifyFast_MissingFileReported()
    {
        WriteFile(_tempDir.FilePath("a.txt"), "hello"u8.ToArray());
        var manifest = new GameManifest
        {
            Version = "1.0.0",
            Files = [FileEntry("a.txt", "hello"u8.ToArray()), FileEntry("gone.txt", "world"u8.ToArray())],
        };

        var result = ManifestVerifier.VerifyFast(_tempDir.Path, manifest);

        var missing = Assert.Single(result.NeedsDownload);
        Assert.Equal("gone.txt", missing.Path);
        Assert.Equal(FileStatus.Missing, missing.Status);
    }

    [Fact]
    public void VerifyFast_SizeMismatchReported()
    {
        var content = "hello"u8.ToArray();
        WriteFile(_tempDir.FilePath("a.txt"), "hell"u8.ToArray());
        var manifest = new GameManifest
        {
            Version = "1.0.0",
            Files = [FileEntry("a.txt", content)],
        };

        var result = ManifestVerifier.VerifyFast(_tempDir.Path, manifest);

        var entry = Assert.Single(result.NeedsDownload);
        Assert.Equal(FileStatus.SizeMismatch, entry.Status);
    }

    [Fact]
    public void VerifyFast_SameSizeWrongContent_Passes()
    {
        WriteFile(_tempDir.FilePath("a.txt"), "XXXXX"u8.ToArray());
        var manifest = new GameManifest
        {
            Version = "1.0.0",
            Files = [FileEntry("a.txt", "hello"u8.ToArray())],
        };

        var result = ManifestVerifier.VerifyFast(_tempDir.Path, manifest);

        Assert.True(result.IsComplete);
    }

    [Fact]
    public void VerifyFull_SameSizeWrongContent_Md5MismatchReported()
    {
        WriteFile(_tempDir.FilePath("a.txt"), "XXXXX"u8.ToArray());
        var manifest = new GameManifest
        {
            Version = "1.0.0",
            Files = [FileEntry("a.txt", "hello"u8.ToArray())],
        };

        var result = ManifestVerifier.VerifyFull(_tempDir.Path, manifest);

        var entry = Assert.Single(result.NeedsDownload);
        Assert.Equal(FileStatus.Md5Mismatch, entry.Status);
    }

    [Fact]
    public void VerifyFull_AllOk()
    {
        var a = "hello"u8.ToArray();
        WriteFile(_tempDir.FilePath("a.txt"), a);
        var manifest = new GameManifest
        {
            Version = "1.0.0",
            Files = [FileEntry("a.txt", a)],
        };

        var result = ManifestVerifier.VerifyFull(_tempDir.Path, manifest);

        Assert.True(result.IsComplete);
    }

    [Fact]
    public void Verify_PathOutsideInstallDir_Throws()
    {
        var manifest = new GameManifest
        {
            Version = "1.0.0",
            Files = [FileEntry("../evil.txt", "x"u8.ToArray())],
        };

        Assert.Throws<InvalidOperationException>(() =>
            ManifestVerifier.VerifyFast(_tempDir.Path, manifest));
    }

    [Fact]
    public void Verify_AbsolutePathInManifest_Throws()
    {
        // 绝对路径必须被拒绝；扎根形式按平台取（"C:/..." 仅在 Windows 扎根，InstallPathTests 同款惯例）
        var absolute = OperatingSystem.IsWindows() ? "C:/Windows/evil.txt" : "/etc/evil.txt";
        var manifest = new GameManifest
        {
            Version = "1.0.0",
            Files = [FileEntry(absolute, "x"u8.ToArray())],
        };

        Assert.Throws<InvalidOperationException>(() =>
            ManifestVerifier.VerifyFast(_tempDir.Path, manifest));
    }

    [Fact]
    public void Verify_WindowsSeparatorNormalized()
    {
        // 清单可能来自 Windows 侧生成，使用反斜杠；应与正斜杠等价处理
        var a = "hello"u8.ToArray();
        WriteFile(_tempDir.FilePath("sub", "b.txt"), a);
        var manifest = new GameManifest
        {
            Version = "1.0.0",
            Files = [new ManifestFile("sub\\b.txt", a.Length, Md5(a))],
        };

        var result = ManifestVerifier.VerifyFast(_tempDir.Path, manifest);

        Assert.True(result.IsComplete);
    }

    [Fact]
    public void NeedsDownload_ContainsAllBrokenFiles()
    {
        WriteFile(_tempDir.FilePath("good.txt"), "aaaa"u8.ToArray());
        WriteFile(_tempDir.FilePath("short.txt"), "a"u8.ToArray());
        var manifest = new GameManifest
        {
            Version = "1.0.0",
            Files =
            [
                FileEntry("good.txt", "aaaa"u8.ToArray()),
                FileEntry("short.txt", "aaaa"u8.ToArray()),
                FileEntry("missing.txt", "aaaa"u8.ToArray()),
            ],
        };

        var result = ManifestVerifier.VerifyFast(_tempDir.Path, manifest);

        Assert.Equal(2, result.NeedsDownload.Count);
        Assert.Equal(["missing.txt", "short.txt"], [.. result.NeedsDownload.Select(f => f.Path).OrderBy(p => p)]);
    }
}

public class ManifestVerifierMissingInfoTests
{
    [Fact]
    public void Verify_SkipsMissingVerificationFields_InsteadOfTreatingHealthyFileAsCorrupt()
    {
        // 回归（2026-09-20 复审）：渠道清单条目缺 size/md5（字段缺失 → 0/空串）时，
        // 磁盘上的健康文件不得按 0/空串判损——否则 VerifyFast/VerifyFull 恒失败，
        // 补下载一轮真内容再校验再失败，更新必然失败（与 HttpFileDownloader.Verify 同语义）
        using var tempDir = new TempDir();
        var path = tempDir.FilePath("a.txt");
        File.WriteAllText(path, "anything non-empty");
        var manifest = new GameManifest
        {
            Version = "1.0.0",
            Files = [new ManifestFile("a.txt", 0, "")],
        };

        var fast = ManifestVerifier.VerifyFast(tempDir.Path, manifest);
        var full = ManifestVerifier.VerifyFull(tempDir.Path, manifest);

        Assert.Equal(FileStatus.Ok, fast.Results[0].Status);
        Assert.True(full.IsComplete);
    }

    [Fact]
    public void CheckFile_StillReportsMissing_WhenVerificationFieldsAbsent()
    {
        // 字段缺失只豁免"值比较"，存在性检查不受影响
        var manifest = new GameManifest
        {
            Version = "1.0.0",
            Files = [new ManifestFile("gone.bin", 0, "")],
        };

        using var goneDir = new TempDir();
        var result = ManifestVerifier.VerifyFast(goneDir.Path, manifest);

        Assert.Equal(FileStatus.Missing, result.Results[0].Status);
    }

    [Fact]
    public async Task CheckFile_F18_RemovedAfterExists_ReportsMissing()
    {
        // F18（artifacts/bugs.md）：Exists→Length/Md5 之间文件被外部移除（手删/云同步隔离/杀毒）
        // 时裸 FileNotFoundException 让整轮校验中止而非按 Missing 走补下载——官方 File.Exists
        // Remarks 明示该 TOCTOU 窗口（"another process can potentially do something with the
        // file in between"）。探测段整体包 catch：修复范围含 Length 与 Md5 两段（第 13 轮补充）
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var manifest = new GameManifest
        {
            Version = "1.0.0",
            Files = [new ManifestFile("data.bin", data.Length, Hashing.Md5Hex(data))],
        };

        using var dir = new TempDir();
        var file = dir.FilePath("data.bin");
        await File.WriteAllBytesAsync(file, data);
        ManifestVerifier.FileRemovedBetweenCheckAndProbeForTests = () => File.Delete(file);
        try
        {
            var status = ManifestVerifier.CheckFile(file, manifest.Files[0], withMd5: true);

            Assert.Equal(FileStatus.Missing, status); // 红：当前 FileNotFoundException 穿出
        }
        finally
        {
            ManifestVerifier.FileRemovedBetweenCheckAndProbeForTests = null;
        }
    }

    [Fact]
    public async Task CheckFile_ParentDirectoryRemovedAfterExists_ReturnsMissingInsteadOfThrowing()
    {
        // 复审 R3（F18 范围扩展，官方 FileInfo.Length 异常表含 DirectoryNotFoundException）：
        // Exists 通过后整个父目录被移除（比单文件删除更狠的 TOCTOU 形态）——同样按 Missing
        // 报告走补下载，不得让整轮校验以裸异常中止
        var data = new byte[] { 1, 2, 3 };
        var manifest = new GameManifest
        {
            Version = "1.0.0",
            Files = [new ManifestFile("sub/data.bin", data.Length, Hashing.Md5Hex(data))],
        };

        using var dir = new TempDir();
        var subdir = dir.FilePath("sub");
        Directory.CreateDirectory(subdir);
        var file = Path.Combine(subdir, "data.bin");
        await File.WriteAllBytesAsync(file, data);
        ManifestVerifier.FileRemovedBetweenCheckAndProbeForTests = () => Directory.Delete(subdir, recursive: true);
        try
        {
            var status = ManifestVerifier.CheckFile(file, manifest.Files[0], withMd5: true);

            Assert.Equal(FileStatus.Missing, status); // 红：当前 DirectoryNotFoundException 穿出
        }
        finally
        {
            ManifestVerifier.FileRemovedBetweenCheckAndProbeForTests = null;
        }
    }

    [Fact]
    public void CheckFile_F44_UnreadableFile_ReportsUnreadableInsteadOfThrowing()
    {
        // F44（artifacts/bugs.md）：chmod 000/ACL 拒读时 Hashing.Md5Hex 的 File.OpenRead 抛
        // UnauthorizedAccessException 裸穿（catch 只接 FNFE/DNFE），整轮校验中止——与 F18 同型
        // 的 best-effort 失守，映射 Unreadable 并入 NeedsDownload 走补下载语义而非炸掉校验轮。
        // 前提自检（AGENTS.md 复审纪律 2）：root/CAP_DAC_OVERRIDE 无视权限位，构造不出拒读形态
        //（正向守卫 + else Skip：CA1416 平台分析器只认 OperatingSystem.IsLinux() 直接分支）
        if (OperatingSystem.IsLinux())
        {
            var data = new byte[] { 7, 8, 9, 10 };
            var manifest = new GameManifest
            {
                Version = "1.0.0",
                Files = [new ManifestFile("locked.bin", data.Length, Hashing.Md5Hex(data))],
            };

            using var dir = new TempDir();
            // root/CAP_DAC_OVERRIDE 豁免 DAC：拒读形态构造不出（同族前提探针）
            if (!DacExemptionProbe.CanConstructDeniedFixture(dir.Path))
            {
                Assert.Skip("当前进程可无视权限位（root/CAP_DAC_OVERRIDE 等能力豁免），拒读形态不成立");
            }

            var file = dir.FilePath("locked.bin");
            File.WriteAllBytes(file, data);
            File.SetUnixFileMode(file, UnixFileMode.None);

            var ex = Record.Exception(() => ManifestVerifier.CheckFile(file, manifest.Files[0], withMd5: true));

            Assert.Null(ex); // 红落此断言：当前 UnauthorizedAccessException 裸穿
            Assert.Equal(FileStatus.Unreadable, ManifestVerifier.CheckFile(file, manifest.Files[0], withMd5: true));
        }
        else
        {
            Assert.Skip("chmod 权限位拒读语义仅 Linux 确定性（Windows ACL 形态不同）");
        }
    }
}
