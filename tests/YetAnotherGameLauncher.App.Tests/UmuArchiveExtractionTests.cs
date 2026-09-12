using System.Formats.Tar;
using System.IO.Compression;
using YetAnotherGameLauncher.Services;
using YetAnotherGameLauncher.TestSupport;
using Xunit;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 原生 umu 组件解包（internal，经 InternalsVisibleTo）：tar.gz 经与线上包相同的 TarReader 链路，
/// 验证权限位还原、符号链接/硬链接、路径穿越拒绝与顶层目录迁移。
/// xz 容器与 gzip 共用同一条 TarReader 落盘路径，仅 OpenDecompressedStream 的魔数分发不同；
/// SharpCompress 无 XZ 编码器，测试内无法构造 xz 流，故以 tar.gz 为代表。
/// </summary>
public sealed class UmuArchiveExtractionTests : IDisposable
{
    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ExtractTarArchive_RestoresModesAndLinks_SkipsTraversal()
    {
        var archive = WriteTarGz(writer =>
        {
            writer.WriteEntry(new UstarTarEntry(TarEntryType.Directory, "top/bin"));
            var exe = new UstarTarEntry(TarEntryType.RegularFile, "top/bin/run.sh");
            exe.Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
            exe.DataStream = new MemoryStream("#!/bin/sh\necho hi\n"u8.ToArray());
            writer.WriteEntry(exe);

            var link = new UstarTarEntry(TarEntryType.SymbolicLink, "top/bin/linked.sh");
            link.LinkName = "run.sh";
            writer.WriteEntry(link);

            var evil = new UstarTarEntry(TarEntryType.RegularFile, "../evil.txt");
            evil.DataStream = new MemoryStream("evil"u8.ToArray());
            writer.WriteEntry(evil);
        });

        var dest = _temp.FilePath("out");
        UmuComponentProvisioner.ExtractTarArchive(archive, dest);

        Assert.True(Directory.Exists(Path.Combine(dest, "top", "bin")));
        Assert.True(File.Exists(Path.Combine(dest, "top", "bin", "run.sh")));
        Assert.Equal("#!/bin/sh\necho hi\n", File.ReadAllText(Path.Combine(dest, "top", "bin", "run.sh")));
        // 路径穿越条目被拒绝，绝不写出目标目录之外
        Assert.False(File.Exists(Path.Combine(dest, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(_temp.Path, "evil.txt")));

        if (OperatingSystem.IsLinux())
        {
            // 执行位按 tar 头原样还原（Proton/Runtime 树能否启动的关键）
            var mode = File.GetUnixFileMode(Path.Combine(dest, "top", "bin", "run.sh"));
            Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
            var link = Path.Combine(dest, "top", "bin", "linked.sh");
            Assert.Equal("run.sh", new FileInfo(link).LinkTarget);
        }
    }

    [Fact]
    public void ExtractTarArchive_HardLinkWithMissingTarget_SkipsSilently()
    {
        var archive = WriteTarGz(writer =>
        {
            var hard = new UstarTarEntry(TarEntryType.HardLink, "top/linked.lib");
            hard.LinkName = "top/missing.lib";
            writer.WriteEntry(hard);
        });

        var dest = _temp.FilePath("out");
        UmuComponentProvisioner.ExtractTarArchive(archive, dest);

        // 硬链接条目无数据段、目标未解出：跳过且不抛
        Assert.False(File.Exists(Path.Combine(dest, "top", "linked.lib")));
    }

    [Fact]
    public void ExtractSingleTopLevel_MovesTopDirIntoTarget()
    {
        var archive = WriteTarGz(writer =>
        {
            writer.WriteEntry(new UstarTarEntry(TarEntryType.Directory, "GE-Proton10-9"));
            writer.WriteEntry(new UstarTarEntry(TarEntryType.Directory, "GE-Proton10-9/files"));
            var file = new UstarTarEntry(TarEntryType.RegularFile, "GE-Proton10-9/files/wine");
            file.DataStream = new MemoryStream("binary"u8.ToArray());
            writer.WriteEntry(file);
        });

        var target = _temp.FilePath("compat", "GE-Proton10-9");
        UmuComponentProvisioner.ExtractSingleTopLevel(archive, target);

        Assert.True(File.Exists(Path.Combine(target, "files", "wine")));
        Assert.False(Directory.Exists(target + ".extract"));
    }

    [Fact]
    public void ExtractSingleTopLevel_FlatArchive_FlattensIntoTarget()
    {
        var archive = WriteTarGz(writer =>
        {
            var file = new UstarTarEntry(TarEntryType.RegularFile, "proton");
            file.DataStream = new MemoryStream("#!/usr/bin/env python3\n"u8.ToArray());
            writer.WriteEntry(file);
        });

        var target = _temp.FilePath("compat", "GE-Proton10-9");
        UmuComponentProvisioner.ExtractSingleTopLevel(archive, target);

        // 无顶层目录的包直接摊开：proton 落在 target 根
        Assert.True(File.Exists(Path.Combine(target, "proton")));
        Assert.False(Directory.Exists(target + ".extract"));
    }

    /// <summary>用 System.Formats.Tar 造 tar.gz 包（与线上 .tar.gz 同为 ustar 条目）。</summary>
    private string WriteTarGz(Action<TarWriter> build)
    {
        var path = _temp.FilePath("pkg.tar.gz");
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.Fastest);
        using var writer = new TarWriter(gz);
        build(writer);
        return path;
    }
}
