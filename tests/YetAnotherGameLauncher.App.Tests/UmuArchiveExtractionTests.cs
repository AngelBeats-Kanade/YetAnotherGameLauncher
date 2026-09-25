using System.Formats.Tar;
using System.IO.Compression;
using Xunit;
using YetAnotherGameLauncher.Services;
using YetAnotherGameLauncher.TestSupport;

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
    public void ExtractTarArchive_HardLinkBeforeTarget_OutOfOrder_StillMaterialized()
    {
        // 次级 suspect（第 9 轮，artifacts/bugs.md）：tar 允许硬链接条目先于目标文件出现（乱序包）——
        // 单趟解压时目标未解出即静默跳过，链接路径整体缺失（运行时树缺文件）。修复 = 链接条目
        // 延迟到普通文件全部落盘后再创建
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("链接创建为 POSIX 语义（Windows 需开发者模式），仅 Linux 确定性");
        }

        var archive = WriteTarGz(writer =>
        {
            var hard = new UstarTarEntry(TarEntryType.HardLink, "top/bin/wine");
            hard.LinkName = "wine-preloader"; // ustar 硬链接目标相对链接所在目录（GNU tar 实包形态）
            writer.WriteEntry(hard); // 先于目标出现
            var target = new UstarTarEntry(TarEntryType.RegularFile, "top/bin/wine-preloader");
            target.DataStream = new MemoryStream("payload"u8.ToArray());
            writer.WriteEntry(target);
        });

        var dest = _temp.FilePath("out");
        UmuComponentProvisioner.ExtractTarArchive(archive, dest);

        Assert.True(File.Exists(Path.Combine(dest, "top", "bin", "wine-preloader")));
        Assert.True(File.Exists(Path.Combine(dest, "top", "bin", "wine"))); // 红落此断言：乱序硬链接被静默跳过
        Assert.Equal("payload", File.ReadAllText(Path.Combine(dest, "top", "bin", "wine")));
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
        // 预置旧代安装（含 .outdated 残留形态的对照）：替换后旧树清掉、不得留 .outdated——
        // 平铺包（source==temp）分支曾因条件守卫跳过清理，旧安装整树滞留一个更新周期
        //（review 立案 2026-09-26 第 2 轮）
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "old-marker"), "old");

        UmuComponentProvisioner.ExtractSingleTopLevel(archive, target);

        // 无顶层目录的包直接摊开：proton 落在 target 根
        Assert.True(File.Exists(Path.Combine(target, "proton")));
        Assert.False(File.Exists(Path.Combine(target, "old-marker")));
        Assert.False(Directory.Exists(target + ".outdated")); // 红落此断言：平铺分支旧形态不清理
        Assert.False(Directory.Exists(target + ".extract"));
    }

    [Fact]
    public void ExtractSingleTopLevel_OutdatedCleanupFailure_InstallStillSucceeds()
    {
        // review 第 3 轮 F4（artifacts/bugs.md）：outdated 清理 fail-soft 化（Directory.Delete →
        // TryDeleteDirectory）的回归钉——旧代清理失败（Windows 占用/权限形态）不得让安装成功
        // 折算成失败。Linux 确定性构造：outdated 含 chmod 000 子目录，硬删除在此抛 UAE 穿出、
        // fail-soft 容忍为 false（残留由下轮 pre-cleanup 重试）。
        // 正向守卫 + else Skip：CA1416 平台分析器只认 OperatingSystem.IsLinux() 直接分支
        if (OperatingSystem.IsLinux())
        {
            // root/CAP_DAC_OVERRIDE 豁免 DAC（对照 FileUtilitiesTests/GB 测试的 root 前提探针）：
            // chmod 000 构造不出清理失败形态，测试会退化平凡通过
            var probePath = _temp.FilePath("dac-probe.txt");
            File.WriteAllText(probePath, "x");
            File.SetUnixFileMode(probePath, UnixFileMode.None);
            try
            {
                _ = File.ReadAllText(probePath);
                Assert.Skip("当前进程可无视权限位（root/CAP_DAC_OVERRIDE），清理失败形态不成立");
            }
            catch (UnauthorizedAccessException)
            {
                // 前提成立：读确实被拒
            }

            var archive = WriteTarGz(writer =>
            {
                writer.WriteEntry(new UstarTarEntry(TarEntryType.Directory, "GE-Proton10-9"));
                var file = new UstarTarEntry(TarEntryType.RegularFile, "GE-Proton10-9/proton");
                file.DataStream = new MemoryStream("#!/bin/sh\n"u8.ToArray());
                writer.WriteEntry(file);
            });

            var target = _temp.FilePath("compat", "GE-Proton10-9");
            var outdated = target + ".outdated";
            Directory.CreateDirectory(Path.Combine(outdated, "locked"));
            File.WriteAllText(Path.Combine(outdated, "locked", "busy"), "x");
            File.SetUnixFileMode(Path.Combine(outdated, "locked"), UnixFileMode.None);
            try
            {
                var ex = Record.Exception(() => UmuComponentProvisioner.ExtractSingleTopLevel(archive, target));

                Assert.Null(ex); // 红落此断言：硬删除形态在此抛 UnauthorizedAccessException
                Assert.True(File.Exists(Path.Combine(target, "proton"))); // 新树完整就位
            }
            finally
            {
                // 还原权限位：TempDir.Dispose 的递归删除（及 owner 的 rm -rf）对 mode-000
                // 目录无能为力，不还原则每跑一次遗留一棵不可删树污染临时目录
                var lockedDir = Path.Combine(outdated, "locked");
                if (Directory.Exists(lockedDir))
                {
                    try
                    {
                        File.SetUnixFileMode(
                            lockedDir,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
        }
        else
        {
            Assert.Skip("chmod 000 构造清理失败仅 Linux 确定性");
        }
    }

    [Fact]
    public void ExtractTarArchive_LinkTargetEscapingDestination_IsSkipped()
    {
        // 回归：链接条目的 LinkName 曾不校验——".." 或绝对路径目标的链接可把后续普通文件
        // 条目经链接写穿到目标目录外（与 zip 条目沙箱同性质的纵深防御）
        var archive = WriteTarGz(writer =>
        {
            var evil = new UstarTarEntry(TarEntryType.SymbolicLink, "top/evil");
            evil.LinkName = "../../outside_link";
            writer.WriteEntry(evil);

            var through = new UstarTarEntry(TarEntryType.RegularFile, "top/evil/pwned.txt");
            through.DataStream = new MemoryStream("pwned"u8.ToArray());
            writer.WriteEntry(through);

            var absolute = new UstarTarEntry(TarEntryType.SymbolicLink, "top/abs");
            absolute.LinkName = "/etc/passwd";
            writer.WriteEntry(absolute);
        });

        var dest = _temp.FilePath("out");
        UmuComponentProvisioner.ExtractTarArchive(archive, dest);

        // 逃逸链接被静默拒绝：目标目录外无任何写入
        Assert.False(File.Exists(Path.Combine(_temp.Path, "outside_link")));
        // 链接被拒后文件条目就地落盘（真实目录，而非经链接写穿）
        Assert.Equal("pwned", File.ReadAllText(Path.Combine(dest, "top", "evil", "pwned.txt")));
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
