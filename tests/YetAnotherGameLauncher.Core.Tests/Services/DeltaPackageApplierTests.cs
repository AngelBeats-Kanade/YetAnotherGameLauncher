using Xunit;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Core.Utilities;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Core.Tests.Services;

/// <summary>
/// v2 增量载荷应用器（终末地形态）：下载分卷 → 差分合成/整文件落位/只读校验 → 删除官方
/// 废弃清单 → 清理载荷。真机实测前提：差分节点与目标零重叠（可就地替换）、verify-only
/// 节点在盘即为满足。
/// </summary>
[Collection("sequential")]
public class DeltaPackageApplierTests : IDisposable
{
    private readonly TempDir _tempDir = new();
    private readonly FakeDownloader _downloader = new();
    private readonly FakeFilePatchApplier _applier = new();

    public void Dispose() => _tempDir.Dispose();

    private const string VolumeName = "payload.zip.001";
    private static string VolumeUrl => $"https://cdn.example.com/delta/{VolumeName}";

    /// <summary>载荷内容：明文整文件条目 + AES 差分条目（夹具片段）+ 明文废弃清单。</summary>
    private static byte[] BuildPayload(string fullFileName, byte[] fullFileContent, string obsoleteList) =>
        TestPayload.BuildPayload(
            TestPayload.PlainEntry(fullFileName, fullFileContent),
            TestPayload.AesEntryFragment,
            TestPayload.PlainEntry(DeltaPackageApplier.ObsoleteListEntryName, System.Text.Encoding.UTF8.GetBytes(obsoleteList)));

    private DeltaPackagePlan BuildPlan(byte[] payload, IReadOnlyList<DeltaPackageFile> files) => new(
        FromVersion: "1.5.3",
        ToVersion: "1.6.4",
        VfsBasePath: "Endfield_Data/StreamingAssets/VFS",
        CdKey: TestPayload.AesPassword,
        Volumes: [new DeltaPackageVolume(VolumeUrl, payload.Length, Hashing.Md5Hex(payload))],
        Files: files,
        StaticEntryExcludedPrefixes: ["vfs_files/", "diff_"],
        StaticEntryExcludedNames: [DeltaPackageApplier.ObsoleteListEntryName, "patch.json", "verify_files.json", "binary.bin"],
        StaticEntryRewrites: new Dictionary<string, string>(StringComparer.Ordinal) { ["config.ini"] = "config.ini.new" });

    private DeltaPackageFile PatchFile(string name, byte[] produced, string baseName, string baseMd5) => new(
        Name: name,
        Md5: Hashing.Md5Hex(produced),
        Size: produced.Length,
        DiffType: 1,
        FullFilePayloadPath: null,
        Patches: [new DeltaPatchSource(
            BaseFile: baseName, BaseMd5: baseMd5, BaseSize: 0, PatchPath: "binary.bin", PatchSize: 0)]);

    private string CreateFile(string relative, string content)
    {
        var path = _tempDir.FilePath([.. relative.Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task ApplyAsync_PatchesCopiesAndVerifiesAgainstManifest()
    {
        // 三类节点全覆盖：差分（hpatchz 产出与清单 md5 一致）、整文件复制、verify-only
        var fullContent = "full-file-content"u8.ToArray();
        var patchTarget = "AA11/bb22.chk";
        var produced = "PATCHED-CONTENT"u8.ToArray();
        var payload = BuildPayload("vfs_files/files/Endfield_Data/x.dat", fullContent, "old/stale1.chk\nold/stale2.chk\n");
        _downloader.Responses[VolumeUrl] = payload;
        var baseFile = CreateFile("Endfield_Data/StreamingAssets/VFS/base/origin.chk", "base-content");
        var verifyOnly = CreateFile("Endfield_Data/StreamingAssets/VFS/keep/keep.chk", "unchanged");
        CreateFile("old/stale1.chk", "stale");
        CreateFile("old/stale2.chk", "stale");
        _applier.Output = produced;
        var plan = BuildPlan(payload,
        [
            PatchFile(patchTarget, produced, "base/origin.chk", Hashing.Md5Hex(baseFile)),
            new DeltaPackageFile("x.dat", Hashing.Md5Hex(fullContent), fullContent.Length, 2,
                "vfs_files/files/Endfield_Data/x.dat", []),
            new DeltaPackageFile("keep/keep.chk", Hashing.Md5Hex(verifyOnly), 0, 1, null, []),
        ]);
        var progress = new UpdateProgressCollector();

        var result = await new DeltaPackageApplier(_downloader, _applier).ApplyAsync(_tempDir.Path, plan, progress);

        Assert.Equal(1, result.Patched);
        Assert.Equal(1, result.Copied);
        Assert.Equal(1, result.Verified);
        Assert.Equal(2, result.Deleted);
        Assert.Equal(produced, await File.ReadAllBytesAsync(
            _tempDir.FilePath("Endfield_Data", "StreamingAssets", "VFS", "AA11", "bb22.chk")));
        Assert.Equal(fullContent, await File.ReadAllBytesAsync(
            _tempDir.FilePath("Endfield_Data", "StreamingAssets", "VFS", "x.dat")));
        Assert.True(File.Exists(verifyOnly));
        Assert.False(File.Exists(_tempDir.FilePath("old", "stale1.chk")));
        // 载荷保留到调用方提交点（2026-10-19 三审）：应用器不自删，应用成功到状态落盘之间
        // 掉线/暂停时续跑才可能零下载收敛
        Assert.True(File.Exists(Path.Combine(DeltaPackageApplier.PayloadDir(_tempDir.Path), VolumeName)));
        Assert.Contains(progress.Frames, f => f.Phase == UpdatePhase.Patching);
        Assert.Contains(progress.Frames, f => f.Phase == UpdatePhase.CleaningUp);
        Assert.Contains(progress.Frames, f => f.Phase == UpdatePhase.Done);
        Assert.False(Directory.Exists(_tempDir.FilePath("vfs_files"))); // 排除前缀内的条目不得当静态整文件落根
        Assert.False(File.Exists(_tempDir.FilePath("vfs_files", "files", "Endfield_Data", "x.dat")));
        Assert.False(File.Exists(_tempDir.FilePath("binary.bin")));
        Assert.False(File.Exists(_tempDir.FilePath(DeltaPackageApplier.ObsoleteListEntryName)));
        // 差分调用形状：基文件 → 载荷补丁 → 目标（产出文件由应用器写出）
        var call = Assert.Single(_applier.Calls);
        Assert.Equal(baseFile, call.BaseFile);
        Assert.EndsWith("bb22.chk.yagl-new", call.OutputFile); // 先产出临时文件，核对后替换
    }

    [Fact]
    public async Task ApplyAsync_SecondRunAfterCompletedApply_SkipsAlreadyAppliedWithoutDownload()
    {
        // 2026-10-19 三审（真实可达窗口）：应用已完成、状态未落盘（进程掉线/暂停落在同步的废弃
        // 清理与调用方写盘之间）后重开。等价形态 = 同一计划连跑两次：载荷与分卷都在场，第二次
        // 必须全部按"已到位"跳过、零下载——若按基 md5 重走差分会被判"不适用"而错误回退整包。
        // 四审补强：第一跑后删掉基文件（上一轮的官方废弃清理真会这么做）——“已到位 → 不再要求
        // 基文件在盘”的排序必须是可失败断言，否则把早退挪到基文件检查之后仍会全绿
        var fullContent = "full-file-content"u8.ToArray();
        var produced = "PATCHED-CONTENT"u8.ToArray();
        var payload = BuildPayload("vfs_files/files/Endfield_Data/x.dat", fullContent, "");
        _downloader.Responses[VolumeUrl] = payload;
        var baseFile = CreateFile("Endfield_Data/StreamingAssets/VFS/base/origin.chk", "base-content");
        var verifyOnly = CreateFile("Endfield_Data/StreamingAssets/VFS/keep/keep.chk", "unchanged");
        _applier.Output = produced;
        var plan = BuildPlan(payload,
        [
            PatchFile("AA11/bb22.chk", produced, "base/origin.chk", Hashing.Md5Hex(baseFile)),
            new DeltaPackageFile("x.dat", Hashing.Md5Hex(fullContent), fullContent.Length, 2,
                "vfs_files/files/Endfield_Data/x.dat", []),
            new DeltaPackageFile("keep/keep.chk", Hashing.Md5Hex(verifyOnly), 0, 1, null, []),
        ]);

        var first = await new DeltaPackageApplier(_downloader, _applier).ApplyAsync(_tempDir.Path, plan);
        Assert.Equal(1, first.Patched);
        var requestsAfterFirst = _downloader.Requests.Count;
        var callsAfterFirst = _applier.Calls.Count;

        // 上一轮的官方废弃清理把旧基文件删掉：续跑仍必须成功（已到位节点不再要求基文件在盘）。
        // 若 EnsureApplicable 的"已到位"早退被挪到基文件检查之后，这里会抛"base file is missing"
        File.Delete(baseFile);
        Assert.False(File.Exists(baseFile));

        var second = await new DeltaPackageApplier(_downloader, _applier).ApplyAsync(_tempDir.Path, plan);

        Assert.Equal(requestsAfterFirst, _downloader.Requests.Count);  // 载荷复用：零网络
        Assert.Equal(callsAfterFirst, _applier.Calls.Count);           // 差分节点零补丁调用（已到位）
        Assert.Equal(0, second.Patched);
        Assert.Equal(0, second.Copied);
        Assert.Equal(3, second.Verified);                              // 三个节点都按"已到位"通过
        Assert.True(File.Exists(Path.Combine(DeltaPackageApplier.PayloadDir(_tempDir.Path), VolumeName)));
    }

    [Fact]
    public async Task ApplyAsync_ResumeAfterMidApplyCancel_PatchesOnlyRemainingFilesWithoutDownload()
    {
        // 2026-10-19 三审：应用中途取消（补丁工具调用之间是真实可暂停点）后重跑——已落位节点
        // 必须按"已到位"跳过，只补未完成节点，且载荷在场零下载
        var produced = "PATCHED-CONTENT"u8.ToArray();
        var payload = BuildPayload("vfs_files/files/unused.bin", "static"u8.ToArray(), "");
        _downloader.Responses[VolumeUrl] = payload;
        var baseA = CreateFile("Endfield_Data/StreamingAssets/VFS/base/a.chk", "base-a");
        var baseB = CreateFile("Endfield_Data/StreamingAssets/VFS/base/b.chk", "base-b");
        var verifyOnly = CreateFile("Endfield_Data/StreamingAssets/VFS/keep/keep.chk", "unchanged");
        _applier.Output = produced;
        var plan = BuildPlan(payload,
        [
            PatchFile("AA11/aa11.chk", produced, "base/a.chk", Hashing.Md5Hex(baseA)),
            PatchFile("BB22/bb22.chk", produced, "base/b.chk", Hashing.Md5Hex(baseB)),
            new DeltaPackageFile("keep/keep.chk", Hashing.Md5Hex(verifyOnly), 0, 1, null, []),
        ]);
        _applier.Failure = new OperationCanceledException();
        _applier.FailOnCallIndex = 1; // 第一个节点应用成功，第二个节点前被暂停

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new DeltaPackageApplier(_downloader, _applier).ApplyAsync(_tempDir.Path, plan));
        var requestsAfterCancel = _downloader.Requests.Count;
        Assert.Equal(2, _applier.Calls.Count); // 失败的那次也已记账（假补丁器先 Calls.Add 再抛）

        _applier.Failure = null;
        var callsAfterCancel = _applier.Calls.Count;

        var resumed = await new DeltaPackageApplier(_downloader, _applier).ApplyAsync(_tempDir.Path, plan);

        Assert.Equal(requestsAfterCancel, _downloader.Requests.Count); // 载荷复用：零网络
        Assert.Equal(callsAfterCancel + 1, _applier.Calls.Count);      // 续跑只补第二个节点（+1 次调用）
        Assert.Equal(1, resumed.Patched);
        Assert.Equal(2, resumed.Verified);                             // 已到位的差分节点 + verify-only
    }

    [Fact]
    public async Task ApplyAsync_IntactStagedVolumes_AreReusedWithoutDownload()
    {
        // review P3-2：原用例断言的是"重跑了下载"，复用分支（IsVolumeIntact）无覆盖——
        // 用"失败后载荷留盘"构造复用场景：第二次运行必须零下载
        var payload = BuildPayload("vfs_files/files/x.dat", "abc"u8.ToArray(), string.Empty);
        _downloader.Responses[VolumeUrl] = payload;
        var plan = BuildPlan(payload, []);
        var service = new DeltaPackageApplier(_downloader, _applier);

        // 首跑：下载成功但应用失败（补丁条目不存在）→ 载荷目录保留，随后被复用
        var failing = plan with
        {
            Files = [PatchFile("AA11/bb22.chk", "x"u8.ToArray(), "base/origin.chk", "0011")],
        };
        CreateFile("Endfield_Data/StreamingAssets/VFS/base/origin.chk", "base-content");
        await Assert.ThrowsAsync<DeltaPackageNotApplicableException>(
            () => service.ApplyAsync(_tempDir.Path, failing));
        var staged = Path.Combine(DeltaPackageApplier.PayloadDir(_tempDir.Path), VolumeName);
        Assert.True(File.Exists(staged), "失败后载荷应留在盘上供复用");
        var requestsAfterFirst = _downloader.Requests.Count;

        var second = await service.ApplyAsync(_tempDir.Path, plan);

        Assert.Equal(requestsAfterFirst, _downloader.Requests.Count); // 复用完好分卷：零下载
        Assert.Equal(0, second.Patched + second.Copied + second.Verified);
    }

    [Fact]
    public async Task ApplyAsync_BaseFileMissing_NotApplicableBeforeAnyDownload()
    {
        // 前置校验（便宜、下载前）：基文件不在盘 → 不适用（由调用方回退整包），一个字节都不下
        var payload = BuildPayload("vfs_files/files/x.dat", "abc"u8.ToArray(), string.Empty);
        _downloader.Responses[VolumeUrl] = payload;
        var plan = BuildPlan(payload, [PatchFile("AA11/bb22.chk", "new"u8.ToArray(), "gone/base.chk", "00112233445566778899aabbccddeeff")]);

        var ex = await Assert.ThrowsAsync<DeltaPackageNotApplicableException>(
            () => new DeltaPackageApplier(_downloader, _applier).ApplyAsync(_tempDir.Path, plan));

        Assert.Contains("base file is missing", ex.Message);
        Assert.Empty(_downloader.Requests);
    }

    [Fact]
    public async Task ApplyAsync_VerifyOnlyFileMissing_NotApplicable()
    {
        var payload = BuildPayload("vfs_files/files/x.dat", "abc"u8.ToArray(), string.Empty);
        _downloader.Responses[VolumeUrl] = payload;
        var plan = BuildPlan(payload, [new DeltaPackageFile("keep/missing.chk", "00", 0, 1, null, [])]);

        await Assert.ThrowsAsync<DeltaPackageNotApplicableException>(
            () => new DeltaPackageApplier(_downloader, _applier).ApplyAsync(_tempDir.Path, plan));
    }

    [Fact]
    public async Task ApplyAsync_BaseContentMismatch_NotApplicable()
    {
        // 基文件内容与官方清单不符（安装被改过）：折算"不适用"而不是给用户技术报错
        var baseFile = CreateFile("Endfield_Data/StreamingAssets/VFS/base/origin.chk", "modified-content");
        var payload = BuildPayload("vfs_files/files/x.dat", "abc"u8.ToArray(), string.Empty);
        _downloader.Responses[VolumeUrl] = payload;
        var plan = BuildPlan(payload,
            [PatchFile("AA11/bb22.chk", "new"u8.ToArray(), "base/origin.chk", Hashing.Md5Hex("official-content"u8.ToArray()))]);

        var ex = await Assert.ThrowsAsync<DeltaPackageNotApplicableException>(
            () => new DeltaPackageApplier(_downloader, _applier).ApplyAsync(_tempDir.Path, plan));

        Assert.Contains("content differs", ex.Message);
        Assert.Empty(_applier.Calls); // 未触达补丁器
        Assert.NotNull(baseFile);
    }

    [Fact]
    public async Task ApplyAsync_ProducedContentMismatch_UnusableInsteadOfPlacing()
    {
        // 应用器产出与官方清单 md5 不符：绝不落位坏内容（目标文件保持原样），且判定为
        // "载荷不可用"由编排回退整包——重试同一份计划不会变好（review 非阻塞 4）
        var baseFile = CreateFile("Endfield_Data/StreamingAssets/VFS/base/origin.chk", "base-content");
        var target = CreateFile("Endfield_Data/StreamingAssets/VFS/AA11/bb22.chk", "old-target");
        var payload = BuildPayload("vfs_files/files/x.dat", "abc"u8.ToArray(), string.Empty);
        _downloader.Responses[VolumeUrl] = payload;
        _applier.Output = "WRONG-CONTENT"u8.ToArray();
        var plan = BuildPlan(payload,
            [PatchFile("AA11/bb22.chk", "expected-content"u8.ToArray(), "base/origin.chk", Hashing.Md5Hex(baseFile))]);

        var ex = await Assert.ThrowsAsync<DeltaPackageUnusableException>(
            () => new DeltaPackageApplier(_downloader, _applier).ApplyAsync(_tempDir.Path, plan));

        Assert.Contains("does not match the official manifest", ex.Message);
        Assert.Equal("old-target", await File.ReadAllTextAsync(target));
        Assert.NotNull(baseFile);
    }

    [Fact]
    public async Task ApplyAsync_PayloadEntryUnreadable_UnusableAndRetryable()
    {
        // review 非阻塞 4：载荷条目读不出 = "载荷不可用"（可回退整包，不形成永久失败）；
        // 载荷暂存留在盘上供下次复用，目标文件绝不被触碰
        var baseFile = CreateFile("Endfield_Data/StreamingAssets/VFS/base/origin.chk", "base-content");
        var target = CreateFile("Endfield_Data/StreamingAssets/VFS/AA11/bb22.chk", "old-target");
        var payload = BuildPayload("vfs_files/files/x.dat", "abc"u8.ToArray(), string.Empty);
        _downloader.Responses[VolumeUrl] = payload;
        var broken = PatchFile("AA11/bb22.chk", "expected"u8.ToArray(), "base/origin.chk", Hashing.Md5Hex(baseFile));
        var node = broken.Patches[0] with { PatchPath = "vfs_files/vfs_patch/diff_1.6.4_1.5.3/missing.chk_patch" };
        var plan = BuildPlan(payload, [broken with { Patches = [node] }]);

        var ex = await Assert.ThrowsAsync<DeltaPackageUnusableException>(
            () => new DeltaPackageApplier(_downloader, _applier).ApplyAsync(_tempDir.Path, plan));

        Assert.Contains("unreadable", ex.Message);
        Assert.Equal("old-target", await File.ReadAllTextAsync(target));
        Assert.True(
            File.Exists(Path.Combine(DeltaPackageApplier.PayloadDir(_tempDir.Path), VolumeName)),
            "载荷暂存应留盘，下一次运行按 size+MD5 复用");
    }

    [Fact]
    public async Task ApplyAsync_ConfigIniStaticEntry_LandsAsConfigIniNew()
    {
        // review 非阻塞 2：静态条目改写分支此前零覆盖——若回归（判定恒 false），config.ini
        // 会被直接覆盖到游戏根（用户可见的配置丢失），而现有用例照样全绿
        var payload = TestPayload.BuildPayload(TestPayload.PlainEntry("config.ini", "[settings]"u8.ToArray()));
        _downloader.Responses[VolumeUrl] = payload;

        var result = await new DeltaPackageApplier(_downloader, _applier).ApplyAsync(_tempDir.Path, BuildPlan(payload, []));

        Assert.Equal(0, result.Patched + result.Copied + result.Verified);
        Assert.False(File.Exists(_tempDir.FilePath("config.ini")));
        Assert.Equal("[settings]", await File.ReadAllTextAsync(_tempDir.FilePath("config.ini.new")));
    }

    [Fact]
    public async Task ApplyAsync_MultipleCandidates_UsesFirstUsableOne()
    {
        // review nit：官方允许多候选（一条目标多个基文件变体）。第一候选基文件缺失、第二候选
        // 内容不符时，必须试第三条可用候选，而不是白退整包（实测 224/224 单候选，形态要对齐实现）
        var wrong = CreateFile("Endfield_Data/StreamingAssets/VFS/base/wrong.chk", "wrong-content");
        var good = CreateFile("Endfield_Data/StreamingAssets/VFS/base/good.chk", "good-content");
        var payload = BuildPayload("vfs_files/files/x.dat", "abc"u8.ToArray(), string.Empty);
        _downloader.Responses[VolumeUrl] = payload;
        var produced = "produced"u8.ToArray();
        _applier.Output = produced;
        var plan = BuildPlan(payload,
        [
            new DeltaPackageFile("AA11/bb22.chk", Hashing.Md5Hex(produced), produced.Length, 1, null,
            [
                new DeltaPatchSource("gone/missing.chk", Hashing.Md5Hex(wrong), 1, "binary.bin", 0),
                new DeltaPatchSource("base/wrong.chk", "00112233445566778899aabbccddeeff", 13, "binary.bin", 0),
                new DeltaPatchSource("base/good.chk", Hashing.Md5Hex(good), 12, "binary.bin", 0),
            ]),
        ]);

        var result = await new DeltaPackageApplier(_downloader, _applier).ApplyAsync(_tempDir.Path, plan);

        Assert.Equal(1, result.Patched);
        var call = Assert.Single(_applier.Calls);
        Assert.Equal(good, call.BaseFile);
    }

    [Fact]
    public async Task ApplyAsync_TempLeafIsSymlink_WritesFreshFileInsteadOfThroughLink()
    {
        // review 非阻塞 5（F19/F43 同族）：临时名（*.yagl-new）被既有符号链接占位时，
        // 直接写会跟随链接落到沙箱之外——写前解除链接，外部目标必须原样
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("符号链接语义在 Windows 需特权，仅 Linux 确定性");
        }

        var outside = _tempDir.FilePath("outside.dll");
        File.WriteAllText(outside, "outside-content");
        var dll = _tempDir.FilePath("GameAssembly.dll");
        File.WriteAllText(dll, "old-dll");
        File.CreateSymbolicLink(dll + ".yagl-new", outside);
        var payload = TestPayload.BuildPayload(TestPayload.PlainEntry("GameAssembly.dll", "new-dll"u8.ToArray()));
        _downloader.Responses[VolumeUrl] = payload;

        await new DeltaPackageApplier(_downloader, _applier).ApplyAsync(_tempDir.Path, BuildPlan(payload, []));

        Assert.Equal("new-dll", await File.ReadAllTextAsync(dll));
        Assert.Equal("outside-content", await File.ReadAllTextAsync(outside));
    }

    [Fact]
    public async Task ApplyAsync_TempLeafIsHardLink_UnlinksBeforeWriting()
    {
        // 硬链接没有 ReparsePoint 标记（F43）：写会截断共享 inode（沙箱外同名数据）——
        // 写前必须先解除该目录项
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("hardlink 创建经 libc link(2)，仅 Linux 确定性");
        }

        var outside = _tempDir.FilePath("outside.bin");
        File.WriteAllText(outside, "outside-content");
        var baseFile = CreateFile("Endfield_Data/StreamingAssets/VFS/base/origin.chk", "base-content");
        var deltaLeaf = _tempDir.FilePath("Endfield_Data", "StreamingAssets", "VFS", "AA11", "bb22.chk.yagl-delta");
        Directory.CreateDirectory(Path.GetDirectoryName(deltaLeaf)!);
        Assert.Equal(0, Link(outside, deltaLeaf)); // 同一 inode 两个名字
        var payload = BuildPayload("vfs_files/files/x.dat", "abc"u8.ToArray(), string.Empty);
        _downloader.Responses[VolumeUrl] = payload;
        _applier.Output = "produced"u8.ToArray();
        var plan = BuildPlan(payload,
            [PatchFile("AA11/bb22.chk", "produced"u8.ToArray(), "base/origin.chk", Hashing.Md5Hex(baseFile))]);

        var result = await new DeltaPackageApplier(_downloader, _applier).ApplyAsync(_tempDir.Path, plan);

        Assert.Equal(1, result.Patched);
        Assert.Equal("produced", await File.ReadAllTextAsync(
            _tempDir.FilePath("Endfield_Data", "StreamingAssets", "VFS", "AA11", "bb22.chk")));
        Assert.Equal("outside-content", await File.ReadAllTextAsync(outside)); // 共享 inode 未被截断
    }

    /// <summary>libc link(2)：测试里制造硬链接（BCL 无 API）；仅 Linux 路径使用。</summary>
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(string existingPath, string newPath);

    [Fact]
    public async Task ApplyAsync_ObsoleteListNamingABaseFile_DeletesItOnlyAfterApply()
    {
        // review P2-1：实测废弃清单 47.72 GiB 里 47.29 GiB 正是差分基文件本身——"先删后补"会毁掉
        // 增量链（224/224 基文件会被删光，直接回退整包）。本用例钉住顺序不变量：
        // 被清单点名的基文件必须先参与应用、随后才由清理步骤删除
        var baseFile = CreateFile("Endfield_Data/StreamingAssets/VFS/base/origin.chk", "base-content");
        var payload = BuildPayload(
            "vfs_files/files/x.dat", "abc"u8.ToArray(),
            "Endfield_Data/StreamingAssets/VFS/base/origin.chk\n");
        _downloader.Responses[VolumeUrl] = payload;
        _applier.Output = "produced"u8.ToArray();
        var plan = BuildPlan(payload,
            [PatchFile("AA11/bb22.chk", "produced"u8.ToArray(), "base/origin.chk", Hashing.Md5Hex(baseFile))]);

        var result = await new DeltaPackageApplier(_downloader, _applier).ApplyAsync(_tempDir.Path, plan);

        Assert.Equal(1, result.Patched); // 应用时基文件还在
        Assert.Equal(baseFile, Assert.Single(_applier.Calls).BaseFile);
        Assert.Equal(1, result.Deleted); // 应用完成后清理才删掉它
        Assert.False(File.Exists(baseFile));
    }

    [Fact]
    public async Task ApplyAsync_PatchToolFailure_UnusableInsteadOfHardError()
    {
        // review P2-3：补丁工具非零退出（补丁内容对不上，载荷侧问题）必须折算"载荷不可用"→ 回退整包，
        // 不再形成同一版本过渡内"重试恒失败"；工具缺失/起不来另归 Supply（见下方两个用例）
        var baseFile = CreateFile("Endfield_Data/StreamingAssets/VFS/base/origin.chk", "base-content");
        var payload = BuildPayload("vfs_files/files/x.dat", "abc"u8.ToArray(), string.Empty);
        _downloader.Responses[VolumeUrl] = payload;
        _applier.Failure = new UpdateException("hpatchz 16: patch run ERROR!");
        var plan = BuildPlan(payload,
            [PatchFile("AA11/bb22.chk", "expected"u8.ToArray(), "base/origin.chk", Hashing.Md5Hex(baseFile))]);

        var ex = await Assert.ThrowsAsync<DeltaPackageUnusableException>(
            () => new DeltaPackageApplier(_downloader, _applier).ApplyAsync(_tempDir.Path, plan));

        Assert.Contains("Patch application failed", ex.Message);
        Assert.NotNull(ex.InnerException);
        Assert.NotNull(baseFile);
    }

    [Fact]
    public async Task ApplyAsync_PatchToolStartFailure_SupplyInsteadOfUnusable()
    {
        // 2026-10-18 分家：进程起不来（Win32Exception：TOCTOU 被删/坏 exe/wine 启动失败）属工具/环境侧
        // → Supply（载荷保留、可修复后重试），不再折算成"载荷不可用"（那会清掉数 GiB 载荷暂存）
        var baseFile = CreateFile("Endfield_Data/StreamingAssets/VFS/base/origin.chk", "base-content");
        var payload = BuildPayload("vfs_files/files/x.dat", "abc"u8.ToArray(), string.Empty);
        _downloader.Responses[VolumeUrl] = payload;
        _applier.Failure = new System.ComponentModel.Win32Exception(2, "文件或目录不存在");
        var plan = BuildPlan(payload,
            [PatchFile("AA11/bb22.chk", "expected"u8.ToArray(), "base/origin.chk", Hashing.Md5Hex(baseFile))]);

        var ex = await Assert.ThrowsAsync<DeltaPackageSupplyException>(
            () => new DeltaPackageApplier(_downloader, _applier).ApplyAsync(_tempDir.Path, plan));

        Assert.Contains("failed to start", ex.Message);
        Assert.NotNull(baseFile);
    }

    [Fact]
    public async Task ApplyAsync_PatchToolUnavailable_SupplyInsteadOfUnusable()
    {
        // 工具缺失/供给失败（HpatchzApplier 折算的专用类型）同样是 Supply：载荷本身是好的
        var baseFile = CreateFile("Endfield_Data/StreamingAssets/VFS/base/origin.chk", "base-content");
        var payload = BuildPayload("vfs_files/files/x.dat", "abc"u8.ToArray(), string.Empty);
        _downloader.Responses[VolumeUrl] = payload;
        _applier.Failure = new PatchToolUnavailableException("hpatchz could not be provisioned: network down");
        var plan = BuildPlan(payload,
            [PatchFile("AA11/bb22.chk", "expected"u8.ToArray(), "base/origin.chk", Hashing.Md5Hex(baseFile))]);

        var ex = await Assert.ThrowsAsync<DeltaPackageSupplyException>(
            () => new DeltaPackageApplier(_downloader, _applier).ApplyAsync(_tempDir.Path, plan));

        Assert.Contains("Patch tool unavailable", ex.Message);
        Assert.NotNull(baseFile);
    }

    [Fact]
    public async Task ApplyAsync_UnreadablePayloadCentralDirectory_Unusable()
    {
        // 复审第六轮 P3-2 的计划级面：载荷中央目录读不出（这里用垃圾字节冒充载荷）必须折算
        // "载荷不可用"回退整包，而不是静默把 dll/exe 漏掉后判成功
        var garbage = new byte[4096];
        Random.Shared.NextBytes(garbage);
        _downloader.Responses[VolumeUrl] = garbage;
        var plan = BuildPlan(garbage, []);

        var ex = await Assert.ThrowsAsync<DeltaPackageUnusableException>(
            () => new DeltaPackageApplier(_downloader, _applier).ApplyAsync(_tempDir.Path, plan));

        Assert.Contains("central directory is unreadable", ex.Message);
    }

    [Fact]
    public async Task ApplyAsync_ManifestPathEscapingRoot_Throws()
    {
        var payload = BuildPayload("vfs_files/files/x.dat", "abc"u8.ToArray(), string.Empty);
        _downloader.Responses[VolumeUrl] = payload;
        var plan = BuildPlan(payload, [new DeltaPackageFile("../../evil.chk", "00", 0, 1, null, [])]);

        await Assert.ThrowsAsync<UpdateException>(
            () => new DeltaPackageApplier(_downloader, _applier).ApplyAsync(_tempDir.Path, plan));
    }

    [Fact]
    public async Task ApplyAsync_ObsoleteEntryCoveringProducedFile_Spared()
    {
        // 清单自相矛盾（废弃清单点名本次产物）：产物优先，不得删刚写出的文件（RF-C 同纪律）。
        // 清单项必须是**产物的真实相对路径**（否则走的只是"文件不存在"分支，守卫形同虚设——review P3-2）
        var fullContent = "full"u8.ToArray();
        var producedRelative = "Endfield_Data/StreamingAssets/VFS/x.dat";
        var payload = BuildPayload("vfs_files/files/x.dat", fullContent, producedRelative + "\n");
        _downloader.Responses[VolumeUrl] = payload;
        var plan = BuildPlan(payload,
            [new DeltaPackageFile("x.dat", Hashing.Md5Hex(fullContent), fullContent.Length, 2, "vfs_files/files/x.dat", [])]);

        var result = await new DeltaPackageApplier(_downloader, _applier).ApplyAsync(_tempDir.Path, plan);

        Assert.Equal(0, result.Deleted);
        Assert.Equal(fullContent, await File.ReadAllBytesAsync(
            _tempDir.FilePath("Endfield_Data", "StreamingAssets", "VFS", "x.dat")));
        Assert.False(Directory.Exists(_tempDir.FilePath("vfs_files")));
        Assert.False(File.Exists(_tempDir.FilePath(DeltaPackageApplier.ObsoleteListEntryName)));
    }

    [Fact]
    public async Task ApplyAsync_StaticEntryCoveredByObsoleteList_Spared()
    {
        // 静态条目（dll 等覆盖游戏根）同样计入产物：官方废弃清单点名它时不得删刚写出的 dll（review P3-1）。
        // 计划里另有一个普通整文件节点，使 produced 非空——否则本用例会被"本轮零产物不清理"守卫
        // 顺手兜住，测不到静态条目的产物记账（P2-2 守卫与本题各自独立，需分别可证伪）
        var otherContent = "other"u8.ToArray();
        var payload = TestPayload.BuildPayload(
            TestPayload.PlainEntry("GameAssembly.dll", "new-dll"u8.ToArray()),
            TestPayload.PlainEntry("vfs_files/files/other.dat", otherContent),
            TestPayload.PlainEntry(DeltaPackageApplier.ObsoleteListEntryName, "GameAssembly.dll\n"u8.ToArray()));
        _downloader.Responses[VolumeUrl] = payload;
        var plan = BuildPlan(payload,
        [
            new DeltaPackageFile("other.dat", Hashing.Md5Hex(otherContent), otherContent.Length, 2, "vfs_files/files/other.dat", []),
        ]);

        var result = await new DeltaPackageApplier(_downloader, _applier).ApplyAsync(_tempDir.Path, plan);

        Assert.Equal(0, result.Deleted);
        Assert.Equal("new-dll", await File.ReadAllTextAsync(_tempDir.FilePath("GameAssembly.dll")));
    }

    [Fact]
    public async Task ApplyAsync_NotEnoughFreeSpace_ActionableErrorBeforeDownload()
    {
        // 2026-10-17 立案③：增量补完所需空间（载荷 + 缺失新内容）曾超过当期空闲——预检必须在下载前
        // 拦下（空间不足不回退整包：整包只会要得更多）
        var payload = BuildPayload("vfs_files/files/x.dat", "abc"u8.ToArray(), string.Empty);
        _downloader.Responses[VolumeUrl] = payload;
        var plan = BuildPlan(payload, []);
        try
        {
            DiskSpace.AvailableFreeBytesForTests = _ => 10; // 远小于载荷（BuildPayload 数十字节起）

            var ex = await Assert.ThrowsAsync<UpdateException>(
                () => new DeltaPackageApplier(_downloader, _applier).ApplyAsync(_tempDir.Path, plan));

            Assert.Contains("Not enough free disk space", ex.Message);
            Assert.Contains("available", ex.Message);
            Assert.Empty(_downloader.Requests); // 预检先于下载
        }
        finally
        {
            DiskSpace.AvailableFreeBytesForTests = null;
        }
    }

    [Fact]
    public async Task ApplyAsync_EnoughFreeSpace_ProceedsNormally()
    {
        // 误杀面：空闲充足时预检必须放行（缝给大值，正常走完应用；载荷保留到调用方提交点）
        var payload = BuildPayload("vfs_files/files/x.dat", "abc"u8.ToArray(), string.Empty);
        _downloader.Responses[VolumeUrl] = payload;
        var plan = BuildPlan(payload, []);
        try
        {
            DiskSpace.AvailableFreeBytesForTests = _ => long.MaxValue;

            var result = await new DeltaPackageApplier(_downloader, _applier).ApplyAsync(_tempDir.Path, plan);

            Assert.Equal(0, result.Verified);
            // 载荷不在应用器内清（2026-10-19 三审）：保留到调用方提交点，续跑才能零下载
            Assert.True(File.Exists(Path.Combine(DeltaPackageApplier.PayloadDir(_tempDir.Path), VolumeName)));
        }
        finally
        {
            DiskSpace.AvailableFreeBytesForTests = null;
        }
    }

    [Fact]
    public async Task ApplyAsync_VerifyOnlyContentMismatch_NotApplicable()
    {
        // 内容级核对（2026-10-18 立案补齐）：尺寸对得上但内容被换过/损坏的"仅校验"节点必须折算
        // "不适用"回退整包——该节点没有载荷，增量无法自愈它。官方 verify_files.json 是二进制
        // 加密块、参考实现也不用，清单自带的目标 md5 是唯一可用判据（红：修复前只核尺寸，静默放行）
        var existing = CreateFile("Endfield_Data/StreamingAssets/VFS/keep/keep.chk", "actual");
        var payload = BuildPayload("vfs_files/files/x.dat", "abc"u8.ToArray(), string.Empty);
        _downloader.Responses[VolumeUrl] = payload;
        var plan = BuildPlan(payload,
        [
            new DeltaPackageFile(
                "keep/keep.chk", Hashing.Md5Hex("expected"u8.ToArray()), "actual"u8.Length, 1, null, []),
        ]);

        var ex = await Assert.ThrowsAsync<DeltaPackageNotApplicableException>(
            () => new DeltaPackageApplier(_downloader, _applier).ApplyAsync(_tempDir.Path, plan));

        Assert.Contains("content differs", ex.Message);
        Assert.True(File.Exists(existing)); // 未被动过
    }

    [Fact]
    public async Task ApplyAsync_VerifyOnlyContentMatches_Verified()
    {
        // 正向面：内容与清单 md5 一致的"仅校验"节点照常通过（尺寸 + 内容双核对不误杀）
        var existing = CreateFile("Endfield_Data/StreamingAssets/VFS/keep/keep.chk", "unchanged");
        var payload = BuildPayload("vfs_files/files/x.dat", "abc"u8.ToArray(), string.Empty);
        _downloader.Responses[VolumeUrl] = payload;
        var plan = BuildPlan(payload,
        [
            new DeltaPackageFile(
                "keep/keep.chk", Hashing.Md5Hex(existing), "unchanged"u8.Length, 1, null, []),
        ]);
        var progress = new UpdateProgressCollector();

        var result = await new DeltaPackageApplier(_downloader, _applier).ApplyAsync(_tempDir.Path, plan, progress);

        Assert.Equal(1, result.Verified);
        Assert.Contains(progress.Frames, f => f.Phase == UpdatePhase.Verifying);
    }

    [Fact]
    public async Task ApplyAsync_VerifyOnlyFileSizeMismatch_NotApplicable()
    {
        // 仅校验节点：在盘还不够，尺寸也要对得上（截断/串位折算"不适用"由编排回退整包，review P3-8d）
        var existing = CreateFile("Endfield_Data/StreamingAssets/VFS/keep/keep.chk", "abc"); // 3 字节
        var payload = BuildPayload("vfs_files/files/x.dat", "abc"u8.ToArray(), string.Empty);
        _downloader.Responses[VolumeUrl] = payload;
        var plan = BuildPlan(payload, [new DeltaPackageFile("keep/keep.chk", Hashing.Md5Hex(existing), 99, 1, null, [])]);

        await Assert.ThrowsAsync<DeltaPackageNotApplicableException>(
            () => new DeltaPackageApplier(_downloader, _applier).ApplyAsync(_tempDir.Path, plan));
    }

    [Fact]
    public async Task ApplyAsync_TargetBehindSymlinkedDirectory_ThrowsInsteadOfWritingThrough()
    {
        // F19 同族（review P3-4）：落点父目录链含既有链接时写出会穿出沙箱——必须折算可读错误且不写穿
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("目录符号链接创建在 Windows 需特权，仅 Linux 确定性");
        }

        var outside = _tempDir.FilePath("outside");
        Directory.CreateDirectory(outside);
        var installDir = _tempDir.FilePath("install");
        Directory.CreateDirectory(installDir);
        Directory.CreateSymbolicLink(Path.Combine(installDir, "linked"), outside);
        var payload = TestPayload.BuildPayload(TestPayload.PlainEntry("linked/evil.bin", "static"u8.ToArray()));
        _downloader.Responses[VolumeUrl] = payload;
        var plan = BuildPlan(payload, []);

        var ex = await Assert.ThrowsAsync<UpdateException>(
            () => new DeltaPackageApplier(_downloader, _applier).ApplyAsync(installDir, plan));

        Assert.Contains("not safe to write", ex.Message);
        Assert.False(File.Exists(Path.Combine(outside, "evil.bin")));
    }
}

/// <summary>单文件补丁器替身：记录调用形状并写出可配置的产出内容。</summary>
public sealed class FakeFilePatchApplier : IFilePatchApplier
{
    public List<(string PatchFile, string BaseFile, string OutputFile)> Calls { get; } = [];

    /// <summary>产出的"新文件"内容（默认空）。</summary>
    public byte[] Output { get; set; } = [];

    /// <summary>非 null 时按补丁工具失败抛出（用于验证"工具失败 → 载荷不可用"折算）。</summary>
    public Exception? Failure { get; set; }

    /// <summary>Failure 的触发下标（0 起；-1 = 每次调用都抛——用于模拟应用中途取消/掉线）。</summary>
    public int FailOnCallIndex { get; set; } = -1;

    public Task ApplyFileAsync(string patchFilePath, string oldFilePath, string newFilePath, CancellationToken cancellationToken = default)
    {
        var callIndex = Calls.Count;
        Calls.Add((patchFilePath, oldFilePath, newFilePath));
        if (Failure is not null && (FailOnCallIndex < 0 || FailOnCallIndex == callIndex))
        {
            throw Failure;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(newFilePath)!);
        File.WriteAllBytes(newFilePath, Output);
        return Task.CompletedTask;
    }
}
