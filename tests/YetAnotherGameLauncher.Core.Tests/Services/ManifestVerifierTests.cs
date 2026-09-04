using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Core.Utilities;
using YetAnotherGameLauncher.TestSupport;
using Xunit;

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
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
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
        var manifest = new GameManifest
        {
            Version = "1.0.0",
            Files = [FileEntry("C:/Windows/evil.txt", "x"u8.ToArray())],
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
