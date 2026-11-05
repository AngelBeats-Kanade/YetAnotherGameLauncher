using System.Text;
using Xunit;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Dependencies;
using YetAnotherGameLauncher.Core.Utilities;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Core.Tests.Dependencies;

/// <summary>
/// winetricks 来源依赖管线：前缀预检 / winetricks 解析 / 备份保护 / 覆盖写入 / DLL 校验 /
/// 失败回滚 / 五态 GetState / 卸载还原。
/// 进程以 <see cref="FakeProcessRunner"/> 替身驱动：wineserver/winetricks/wine reg 全部按 argv 分派，
/// 文件系统用真实临时目录（备份/符号链接/哈希都是被测行为本身）。
/// 真机（真实 Proton + winetricks 下载）只在验收清单跑，不放进离线套件。
/// </summary>
public class DependencyInstallerWinetricksTests : IDisposable
{
    private readonly WinetricksFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    // ── 安装：成功路径与备份形态 ─────────────────────────────────────────────

    /// <summary>成功路径（Proton 符号链接原始态）：argv/env 正确、DLL 变真实文件、覆盖写入、
    /// marker 最后写、备份记录原链接 target 且未写穿。</summary>
    [Fact]
    public async Task Install_HappyPath_SymlinkBackup_WinetricksArgvEnv_FilesOverrideMarker()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("符号链接创建需要特权（Windows）");
            return;
        }

        _fx.InitPrefix(WinetricksFixture.OriginalKind.Symlink);

        await _fx.InstallAsync();

        // winetricks argv 逐项传递（路径含空格也不拆分），--unattended --force + verb
        Assert.NotNull(_fx.WinetricksArgs);
        Assert.Equal(["--unattended", "--force", "testverb"], _fx.WinetricksArgs);
        Assert.Equal(_fx.Prefix, _fx.WinetricksEnvironment!["WINEPREFIX"]);
        Assert.Equal(_fx.WineExecutable, _fx.WinetricksEnvironment["WINE"]);
        Assert.Equal(_fx.Wineserver, _fx.WinetricksEnvironment["WINESERVER"]);
        Assert.Equal("mscoree,mshtml=", _fx.WinetricksEnvironment["WINEDLLOVERRIDES"]);

        // 超时常量：winetricks 首次下载给足 1h；wineserver -w 忙碌检查 10s（策略钉住）
        var winetricksSpec = Assert.Single(
            _fx.Runner.Specs, s => Path.GetFileName(s.FileName) == "winetricks");
        Assert.Equal(3_600_000, winetricksSpec.TimeoutMilliseconds);
        var busySpec = Assert.Single(
            _fx.Runner.Specs, s => Path.GetFileName(s.FileName) == "wineserver");
        Assert.Equal(10_000, busySpec.TimeoutMilliseconds);

        // DLL：真实文件（非链接）+ 期望内容
        Assert.True(File.Exists(_fx.System32Dll));
        Assert.True(File.Exists(_fx.SysWow64Dll));
        Assert.Null(new FileInfo(_fx.System32Dll).LinkTarget);
        Assert.Null(new FileInfo(_fx.SysWow64Dll).LinkTarget);
        Assert.Equal(_fx.X64Bytes, await File.ReadAllBytesAsync(_fx.System32Dll));
        Assert.Equal(_fx.X86Bytes, await File.ReadAllBytesAsync(_fx.SysWow64Dll));

        // 覆盖持久化
        Assert.Equal("native,builtin", _fx.Registry["*msftedit"]);

        // marker 内容
        Assert.Equal(
            "test-native-dll@1.0",
            await File.ReadAllTextAsync(Path.Combine(_fx.StateDir, "test-native-dll.ok")));

        // marker 最后写：winetricks 执行时 marker 必须还不存在
        Assert.False(_fx.MarkerSeenDuringWinetricks, "marker 在 winetricks 之前就写了");

        // 备份：记录原符号链接 target，未被 File.Copy 写穿（Proton 树内容不变）
        var backup = _fx.LoadBackup();
        var x64 = Assert.Single(backup.Dlls, d => d.Arch == "x64");
        Assert.Equal(DependencyBackupKind.Symlink, x64.Kind);
        Assert.Equal(_fx.LibX64, x64.LinkTarget);
        Assert.Equal("builtin x64", await File.ReadAllTextAsync(_fx.LibX64));
    }

    /// <summary>原始是真实文件：备份复制 + 失败回滚按字节还原（不误当符号链接）。</summary>
    [Fact]
    public async Task Install_RealFiles_BackedUpAndRestoredWhenWinetricksFails()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        _fx.FailWinetricks = true;

        var ex = await Assert.ThrowsAsync<DependencyException>(() => _fx.InstallAsync());

        Assert.Equal(DependencyFailureKind.WinetricksFailed, ex.Kind);
        Assert.Contains("download failed", ex.Message);
        Assert.Contains("完整日志", ex.Message);
        Assert.True(File.Exists(Path.Combine(_fx.StateDir, "test-native-dll-winetricks.log")),
            "winetricks 失败日志未落盘（错误消息里的完整日志路径不可用）");
        Assert.Equal("builtin x64", await File.ReadAllTextAsync(_fx.System32Dll));
        Assert.Equal("builtin x86", await File.ReadAllTextAsync(_fx.SysWow64Dll));
        Assert.False(File.Exists(Path.Combine(_fx.StateDir, "test-native-dll.ok")));
        Assert.False(Directory.Exists(_fx.BackupDir));
        Assert.DoesNotContain("*msftedit", _fx.Registry.Keys);
    }

    /// <summary>原始 DLL 缺失：回滚删除 winetricks 新拷入的文件（不留下半成品）。</summary>
    [Fact]
    public async Task Install_MissingOriginals_RollbackDeletesCopiedFiles()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.Missing);
        _fx.FailOverride = true;

        var ex = await Assert.ThrowsAsync<DependencyException>(() => _fx.InstallAsync());

        Assert.Equal(DependencyFailureKind.OverrideFailed, ex.Kind);
        Assert.False(File.Exists(_fx.System32Dll));
        Assert.False(File.Exists(_fx.SysWow64Dll));
        Assert.False(Directory.Exists(_fx.BackupDir));
        Assert.False(File.Exists(Path.Combine(_fx.StateDir, "test-native-dll.ok")));
    }

    /// <summary>覆盖写入失败：DLL 已由 winetricks 拷入，回滚必须恢复原真实文件。</summary>
    [Fact]
    public async Task Install_OverrideWriteFails_RollsBack()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        _fx.FailOverride = true;

        var ex = await Assert.ThrowsAsync<DependencyException>(() => _fx.InstallAsync());

        Assert.Equal(DependencyFailureKind.OverrideFailed, ex.Kind);
        Assert.Equal("builtin x64", await File.ReadAllTextAsync(_fx.System32Dll));
        Assert.False(Directory.Exists(_fx.BackupDir));
    }

    /// <summary>winetricks「成功」但 DLL 尺寸/哈希不符：按 DllVerifyFailed 失败并回滚。</summary>
    [Fact]
    public async Task Install_DllVerifyFails_RollsBack()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        _fx.CorruptWinetricksDll = true;

        var ex = await Assert.ThrowsAsync<DependencyException>(() => _fx.InstallAsync());

        Assert.Equal(DependencyFailureKind.DllVerifyFailed, ex.Kind);
        Assert.Equal("builtin x64", await File.ReadAllTextAsync(_fx.System32Dll));
        Assert.False(Directory.Exists(_fx.BackupDir));
        Assert.False(File.Exists(Path.Combine(_fx.StateDir, "test-native-dll.ok")));
    }

    /// <summary>winetricks 全缺：在任何写操作（含 wineserver）之前失败。</summary>
    [Fact]
    public async Task Install_WinetricksMissing_FailsBeforeAnyWrite()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        _fx.WinetricksPath = null;

        var ex = await Assert.ThrowsAsync<DependencyException>(() => _fx.InstallAsync());

        Assert.Equal(DependencyFailureKind.WinetricksMissing, ex.Kind);
        Assert.Empty(_fx.Runner.Specs);
        Assert.False(Directory.Exists(_fx.BackupDir));
    }

    /// <summary>PrefixBusy：wineserver -w 超时/仍有进程 → 在任何写操作之前失败。</summary>
    [Fact]
    public async Task Install_PrefixBusy_FailsBeforeAnyWrite()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        _fx.Busy = true;

        var ex = await Assert.ThrowsAsync<DependencyException>(() => _fx.InstallAsync());

        Assert.Equal(DependencyFailureKind.PrefixBusy, ex.Kind);
        Assert.DoesNotContain(_fx.Runner.Specs, s => Path.GetFileName(s.FileName) == "winetricks");
        Assert.False(Directory.Exists(_fx.BackupDir));
        Assert.False(File.Exists(Path.Combine(_fx.StateDir, "test-native-dll.ok")));
        Assert.Equal("builtin x64", await File.ReadAllTextAsync(_fx.System32Dll));
    }

    /// <summary>winetricks 阶段取消：回滚到原状且 OCE 透传（回滚用 None，不能被取消跳过）。</summary>
    [Fact]
    public async Task Install_CancelDuringWinetricks_RollsBack()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        _fx.CancelWinetricks = true;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _fx.InstallAsync());

        Assert.Equal("builtin x64", await File.ReadAllTextAsync(_fx.System32Dll));
        Assert.Equal("builtin x86", await File.ReadAllTextAsync(_fx.SysWow64Dll));
        Assert.False(Directory.Exists(_fx.BackupDir));
        Assert.False(File.Exists(Path.Combine(_fx.StateDir, "test-native-dll.ok")));
        Assert.DoesNotContain("*msftedit", _fx.Registry.Keys);
    }

    /// <summary>多个 dllOverrides 条目：全部被备份/写入，回滚时全部恢复（不硬编码 *msftedit）。</summary>
    [Fact]
    public async Task Install_MultipleOverrides_BackedUpWrittenAndRestoredOnRollback()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        _fx.Registry["*second"] = "builtin";
        _fx.FailOverrideOnValue = "native"; // 第二个键写入失败 → 触发回滚

        var ex = await Assert.ThrowsAsync<DependencyException>(() => _fx.InstallAsync());

        Assert.Equal(DependencyFailureKind.OverrideFailed, ex.Kind);
        Assert.Equal("builtin", _fx.Registry["*second"]);
        Assert.DoesNotContain("*msftedit", _fx.Registry.Keys);
        Assert.False(Directory.Exists(_fx.BackupDir));
    }

    // ── 前缀预检 ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Install_Win32Prefix_PrefixUnsupported_ZeroModification()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile, arch: "#arch=win32");

        var ex = await Assert.ThrowsAsync<DependencyException>(() => _fx.InstallAsync());

        Assert.Equal(DependencyFailureKind.PrefixUnsupported, ex.Kind);
        Assert.Empty(_fx.Runner.Specs);
        Assert.False(Directory.Exists(_fx.BackupDir));
        Assert.Equal("builtin x64", await File.ReadAllTextAsync(_fx.System32Dll));
    }

    /// <summary>win64 但缺 syswow64：损坏的 win64 prefix → PrefixUnhealthy，不能误报“不支持”。</summary>
    [Fact]
    public async Task Install_Win64PrefixWithoutSyswow64_PrefixUnhealthy()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        Directory.Delete(_fx.SysWow64Dir, recursive: true);

        var ex = await Assert.ThrowsAsync<DependencyException>(() => _fx.InstallAsync());

        Assert.Equal(DependencyFailureKind.PrefixUnhealthy, ex.Kind);
        Assert.Empty(_fx.Runner.Specs);
    }

    [Fact]
    public async Task Install_SystemRegMissing_PrefixMissing()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        File.Delete(_fx.SystemRegPath);

        var ex = await Assert.ThrowsAsync<DependencyException>(() => _fx.InstallAsync());

        Assert.Equal(DependencyFailureKind.PrefixMissing, ex.Kind);
    }

    /// <summary>system.reg 存在但首部无 #arch（损坏）：PrefixUnhealthy，不是 PrefixMissing。</summary>
    [Fact]
    public async Task Install_SystemRegWithoutArch_PrefixUnhealthy()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile, arch: null);

        var ex = await Assert.ThrowsAsync<DependencyException>(() => _fx.InstallAsync());

        Assert.Equal(DependencyFailureKind.PrefixUnhealthy, ex.Kind);
    }

    // ── 崩溃恢复 / 备份保护 ─────────────────────────────────────────────────

    /// <summary>有备份 + 有 marker（损坏态或异常重入）：不得重新备份，RecoveryFailed 且零写入。</summary>
    [Fact]
    public async Task Install_BackupWithMarker_RecoveryFailed_ZeroWrites()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        await _fx.InstallAsync();
        // 弄坏 DLL 让 GetState=Damaged（否则已装短路直接返回）
        await File.WriteAllBytesAsync(_fx.System32Dll, [0x00]);
        var specsBefore = _fx.Runner.Specs.Count;
        var winetricksCallsBefore = _fx.WinetricksCallCount;

        var ex = await Assert.ThrowsAsync<DependencyException>(() => _fx.InstallAsync());

        Assert.Equal(DependencyFailureKind.RecoveryFailed, ex.Kind);
        // 只允许只读探测（GetState 的 reg query + wineserver -w），不得有 reg add/winetricks 等写操作
        Assert.All(
            _fx.Runner.Specs.Skip(specsBefore),
            spec => Assert.True(
                Path.GetFileName(spec.FileName) == "wineserver"
                || (Path.GetFileName(spec.FileName) == "wine"
                    && spec.ArgumentList is ["reg", "query", ..]),
                $"RecoveryFailed 后出现写操作：{spec.FileName} {string.Join(' ', spec.ArgumentList ?? [])}"));
        Assert.Equal(winetricksCallsBefore, _fx.WinetricksCallCount);
        Assert.Equal(new byte[] { 0x00 }, await File.ReadAllBytesAsync(_fx.System32Dll));
        // 原始备份未被当前真实 DLL 覆盖：备份记录的仍是安装前的真实文件
        var backup = _fx.LoadBackup();
        Assert.Equal(DependencyBackupKind.File, Assert.Single(backup.Dlls, d => d.Arch == "x64").Kind);
    }

    /// <summary>有 marker + 无备份：BackupMissing，不安装、不重新备份。</summary>
    [Fact]
    public async Task Install_MarkerWithoutBackup_BackupMissing_ZeroWrites()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        await _fx.InstallAsync();
        Directory.Delete(_fx.BackupDir, recursive: true);
        var winetricksCallsBefore = _fx.WinetricksCallCount;

        var ex = await Assert.ThrowsAsync<DependencyException>(() => _fx.InstallAsync());

        Assert.Equal(DependencyFailureKind.BackupMissing, ex.Kind);
        Assert.Equal(winetricksCallsBefore, _fx.WinetricksCallCount);
        Assert.False(Directory.Exists(_fx.BackupDir));
    }

    /// <summary>有备份 + 无 marker（上次安装中断）：InstallAsync 先恢复再安装，不覆盖原始备份。</summary>
    [Fact]
    public async Task Install_BackupWithoutMarker_RestoresThenInstalls()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        await _fx.InstallAsync();
        var firstBackup = _fx.LoadBackup();
        File.Delete(Path.Combine(_fx.StateDir, "test-native-dll.ok"));
        // 模拟上次安装中断后的半成品状态：覆盖值仍在、DLL 已是原生文件
        var winetricksCallsBefore = _fx.WinetricksCallCount;

        await _fx.InstallAsync();

        var secondBackup = _fx.LoadBackup();
        // 重新备份的是「已恢复的原始真实文件」，而不是上次装好的原生 DLL
        Assert.Equal(firstBackup.Dlls[0].Sha256, secondBackup.Dlls[0].Sha256);
        Assert.Equal(winetricksCallsBefore + 1, _fx.WinetricksCallCount);
        Assert.True(File.Exists(Path.Combine(_fx.StateDir, "test-native-dll.ok")));
    }

    // ── GetState 五态 ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetState_Installed_RequiresFilesHashesOverridesAndBackup()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        await _fx.InstallAsync();
        var installer = _fx.CreateInstaller();

        var state = installer.GetState(_fx.Target, _fx.Manifest);
        Assert.Equal(DependencyStateKind.Installed, state.Kind);
        Assert.Equal("1.0", state.InstalledVersion);

        // 尺寸不符
        await File.WriteAllBytesAsync(_fx.System32Dll, [0x00]);
        Assert.Equal(DependencyStateKind.Damaged, installer.GetState(_fx.Target, _fx.Manifest).Kind);

        // 同长度但内容不同 → 只有 SHA-256 分支能识别（变异击杀位）
        var corrupted = (byte[])_fx.X64Bytes.Clone();
        corrupted[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(_fx.System32Dll, corrupted);
        Assert.Equal(DependencyStateKind.Damaged, installer.GetState(_fx.Target, _fx.Manifest).Kind);
        await File.WriteAllBytesAsync(_fx.System32Dll, _fx.X64Bytes);

        // 覆盖值存在但不匹配（不是删除）：值比较分支的变异击杀位
        _fx.Registry["*msftedit"] = "builtin";
        Assert.Equal(DependencyStateKind.Damaged, installer.GetState(_fx.Target, _fx.Manifest).Kind);
        _fx.Registry.Remove("*msftedit");
        Assert.Equal(DependencyStateKind.Damaged, installer.GetState(_fx.Target, _fx.Manifest).Kind);

        // 整键不存在（reg query 退出码 1）但父键可读 → 明确是覆盖缺失，仍按 Damaged
        _fx.RegistryKeyExists = false;
        Assert.Equal(DependencyStateKind.Damaged, installer.GetState(_fx.Target, _fx.Manifest).Kind);
        _fx.RegistryKeyExists = true;
        _fx.Registry["*msftedit"] = "native,builtin";

        // 标记版本不一致 → Damaged（可修复），不是未安装
        await File.WriteAllTextAsync(
            Path.Combine(_fx.StateDir, "test-native-dll.ok"), "test-native-dll@0.9");
        Assert.Equal(DependencyStateKind.Damaged, installer.GetState(_fx.Target, _fx.Manifest).Kind);
        await File.WriteAllTextAsync(
            Path.Combine(_fx.StateDir, "test-native-dll.ok"), "test-native-dll@1.0");

        // 备份清单缺失
        File.Delete(_fx.BackupManifestPath);
        Assert.Equal(
            DependencyStateKind.BackupMissing,
            installer.GetState(_fx.Target, _fx.Manifest).Kind);
    }

    /// <summary>覆盖值查询超时/启动失败是“无法确认”，不是“值不匹配”：保持 Installed 并置
    /// VerificationUnavailable，避免 UI 把健康安装误报为 Damaged 引导重装。</summary>
    [Fact]
    public async Task GetState_OverrideQueryTimesOut_InstalledWithVerificationUnavailable()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        await _fx.InstallAsync();
        _fx.FailOverrideQueryWithTimeout = true;

        var state = _fx.CreateInstaller().GetState(_fx.Target, _fx.Manifest);

        Assert.Equal(DependencyStateKind.Installed, state.Kind);
        Assert.Equal("1.0", state.InstalledVersion);
        Assert.True(state.VerificationUnavailable);
    }

    /// <summary>reg query 非零退出且父键探针也失败（如 Wine 53 / prefix 损坏）：无法确认覆盖值，
    /// 不能按 Damaged 引导重装；只有父键可读、确认只是键/值缺失时才报 Mismatched。</summary>
    [Fact]
    public async Task GetState_OverrideQueryNonZeroAndParentProbeFails_InstalledWithVerificationUnavailable()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        await _fx.InstallAsync();
        _fx.WineBroken = true;

        var state = _fx.CreateInstaller().GetState(_fx.Target, _fx.Manifest);

        Assert.Equal(DependencyStateKind.Installed, state.Kind);
        Assert.True(state.VerificationUnavailable);
    }

    /// <summary>目标被换成符号链接（常见误用：cp 写穿后又指回 Proton）：不算真实安装。</summary>
    [Fact]
    public async Task GetState_SymlinkInsteadOfRealFile_Damaged()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("符号链接创建需要特权（Windows）");
            return;
        }

        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        await _fx.InstallAsync();
        // 链接目标换成与期望 DLL 同 size/SHA-256 的副本：只有 LinkTarget 判据能识别出"非真实文件"，
        // 尺寸/哈希分支不会顺带替它变红（变异击杀位）
        await File.WriteAllBytesAsync(_fx.LibX64, _fx.X64Bytes);
        File.Delete(_fx.System32Dll);
        File.CreateSymbolicLink(_fx.System32Dll, _fx.LibX64);

        Assert.Equal(
            DependencyStateKind.Damaged,
            _fx.CreateInstaller().GetState(_fx.Target, _fx.Manifest).Kind);
    }

    [Fact]
    public async Task GetState_BackupManifestCorrupt_BackupMissing()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        await _fx.InstallAsync();
        await File.WriteAllTextAsync(_fx.BackupManifestPath, "{ not json");

        Assert.Equal(
            DependencyStateKind.BackupMissing,
            _fx.CreateInstaller().GetState(_fx.Target, _fx.Manifest).Kind);

        // 无 marker + 损坏清单：仍按 BackupMissing，不能误报"未安装"而允许重新备份
        File.Delete(Path.Combine(_fx.StateDir, "test-native-dll.ok"));
        Assert.Equal(
            DependencyStateKind.BackupMissing,
            _fx.CreateInstaller().GetState(_fx.Target, _fx.Manifest).Kind);
    }

    [Fact]
    public async Task GetState_NoMarkerWithValidBackup_NeedsRecovery()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        await _fx.InstallAsync();
        File.Delete(Path.Combine(_fx.StateDir, "test-native-dll.ok"));

        var state = _fx.CreateInstaller().GetState(_fx.Target, _fx.Manifest);

        Assert.Equal(DependencyStateKind.NeedsRecovery, state.Kind);
    }

    [Fact]
    public void GetState_NoMarkerNoBackup_NotInstalled()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);

        var state = _fx.CreateInstaller().GetState(_fx.Target, _fx.Manifest);

        Assert.Equal(DependencyStateKind.NotInstalled, state.Kind);
    }

    /// <summary>硬杀发生在 CreateBackupAsync 写 manifest 之前：备份目录可能为空，marker 尚未写入。
    /// 此时 prefix 仍是安装前原状，必须按未安装处理，不能引导用户用半成品副本“还原”。</summary>
    [Fact]
    public void GetState_PreCommitResidue_EmptyBackupDir_NotInstalledAndOriginalsIntact()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        _fx.Registry["*msftedit"] = "builtin";
        Directory.CreateDirectory(_fx.BackupDir);
        File.WriteAllText(
            Path.Combine(_fx.BackupDir, DependencyPaths.BackupPendingFileName), "pending");

        var state = _fx.CreateInstaller().GetState(_fx.Target, _fx.Manifest);

        Assert.Equal(DependencyStateKind.NotInstalled, state.Kind);
        Assert.Equal("builtin x64", File.ReadAllText(_fx.System32Dll));
        Assert.Equal("builtin x86", File.ReadAllText(_fx.SysWow64Dll));
        Assert.Equal("builtin", _fx.Registry["*msftedit"]);
    }

    /// <summary>manifest 未提交、只复制了一份 DLL：重装必须清理/覆盖残留，并以当前 prefix 的
    /// 原始文件与旧覆盖建立备份；卸载后仍恢复原状，不能把残留副本当成原始态。</summary>
    [Fact]
    public async Task Install_PreCommitResidue_PartialCopy_BacksUpCurrentOriginalsThenRestores()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        _fx.Registry["*msftedit"] = "builtin";
        Directory.CreateDirectory(_fx.BackupDir);
        await File.WriteAllTextAsync(
            Path.Combine(_fx.BackupDir, DependencyPaths.BackupPendingFileName), "pending");
        await File.WriteAllTextAsync(
            Path.Combine(_fx.BackupDir, "x64-msftedit.dll"), "builtin x64");

        Assert.Equal(
            DependencyStateKind.NotInstalled,
            _fx.CreateInstaller().GetState(_fx.Target, _fx.Manifest).Kind);
        Assert.Equal("builtin x64", await File.ReadAllTextAsync(_fx.System32Dll));
        Assert.Equal("builtin", _fx.Registry["*msftedit"]);

        await _fx.InstallAsync();

        Assert.False(File.Exists(
            Path.Combine(_fx.BackupDir, DependencyPaths.BackupPendingFileName)));
        var backup = _fx.LoadBackup();
        Assert.Contains(
            backup.Dlls,
            d => d.Arch == "x64"
                 && d.Kind == DependencyBackupKind.File
                 && d.Sha256 == Hashing.Sha256Hex(Path.Combine(_fx.BackupDir, "x64-msftedit.dll")));
        Assert.Equal(
            "builtin",
            backup.Overrides.Single(o => o.Key == "*msftedit").OldValue);

        await _fx.UninstallAsync();

        Assert.Equal("builtin x64", await File.ReadAllTextAsync(_fx.System32Dll));
        Assert.Equal("builtin x86", await File.ReadAllTextAsync(_fx.SysWow64Dll));
        Assert.Equal("builtin", _fx.Registry["*msftedit"]);
        Assert.False(Directory.Exists(_fx.BackupDir));
    }

    /// <summary>没有 .incomplete 哨兵时 marker+manifest 同时缺失不能当成预提交：可能是安装成功后
    /// 丢失（prefix 已是原生 DLL），必须按 BackupMissing 保留备份并禁止自动重新备份。</summary>
    [Fact]
    public async Task PostInstallLoss_MarkerAndManifestGone_BackupMissingAndNoRebackup()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        _fx.Registry["*msftedit"] = "builtin";
        await _fx.InstallAsync();
        var backupX64 = Path.Combine(_fx.BackupDir, "x64-msftedit.dll");
        var backupX86 = Path.Combine(_fx.BackupDir, "x86-msftedit.dll");
        var originalX64 = await File.ReadAllBytesAsync(backupX64);
        var originalX86 = await File.ReadAllBytesAsync(backupX86);
        File.Delete(Path.Combine(_fx.StateDir, "test-native-dll.ok"));
        File.Delete(_fx.BackupManifestPath);
        Assert.False(File.Exists(
            Path.Combine(_fx.BackupDir, DependencyPaths.BackupPendingFileName)));

        var installer = _fx.CreateInstaller();
        Assert.Equal(
            DependencyStateKind.BackupMissing,
            installer.GetState(_fx.Target, _fx.Manifest).Kind);

        var ex = await Assert.ThrowsAsync<DependencyException>(
            () => installer.InstallAsync(_fx.Target, _fx.Manifest));

        Assert.Equal(DependencyFailureKind.BackupMissing, ex.Kind);
        Assert.Equal(_fx.X64Bytes, await File.ReadAllBytesAsync(_fx.System32Dll));
        Assert.Equal("native,builtin", _fx.Registry["*msftedit"]);
        Assert.Equal(originalX64, await File.ReadAllBytesAsync(backupX64));
        Assert.Equal(originalX86, await File.ReadAllBytesAsync(backupX86));

        // 手动恢复：副本 + dlloverrides.reg 回原状后再删备份目录，才可以重新安装。
        File.Copy(backupX64, _fx.System32Dll, overwrite: true);
        File.Copy(backupX86, _fx.SysWow64Dll, overwrite: true);
        _fx.Registry["*msftedit"] = "builtin";
        _fx.Registry.Remove("*second");
        Directory.Delete(_fx.BackupDir, recursive: true);

        Assert.Equal("builtin x64", await File.ReadAllTextAsync(_fx.System32Dll));
        Assert.Equal("builtin x86", await File.ReadAllTextAsync(_fx.SysWow64Dll));
        Assert.Equal(
            DependencyStateKind.NotInstalled,
            installer.GetState(_fx.Target, _fx.Manifest).Kind);
    }

    /// <summary>无哨兵、无 marker/manifest 的备份目录（无法证明是预提交）：保守报 BackupMissing，
    /// 不清理、不允许重装备份当前 prefix。</summary>
    [Fact]
    public void GetState_NoMarkerNoManifestNoPending_BackupMissing()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        Directory.CreateDirectory(_fx.BackupDir);

        var state = _fx.CreateInstaller().GetState(_fx.Target, _fx.Manifest);

        Assert.Equal(DependencyStateKind.BackupMissing, state.Kind);
        Assert.Equal("builtin x64", File.ReadAllText(_fx.System32Dll));
    }

    /// <summary>无哨兵时即使当前还是原状也不能自动重装：可能刚被手动清理过 marker/manifest，
    /// 必须保留备份目录等待人工确认。</summary>
    [Fact]
    public async Task Install_NoSentinelNoManifest_BackupMissingAndBackupKept()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        _fx.Registry["*msftedit"] = "builtin";
        Directory.CreateDirectory(_fx.BackupDir);
        await File.WriteAllTextAsync(
            Path.Combine(_fx.BackupDir, "x64-msftedit.dll"), "builtin x64");

        var ex = await Assert.ThrowsAsync<DependencyException>(() => _fx.InstallAsync());

        Assert.Equal(DependencyFailureKind.BackupMissing, ex.Kind);
        Assert.True(Directory.Exists(_fx.BackupDir));
        Assert.Equal("builtin x64", await File.ReadAllTextAsync(_fx.System32Dll));
        Assert.Equal("builtin", _fx.Registry["*msftedit"]);
    }

    /// <summary>哨兵仍在但覆盖已是 App 写入值（DLL 被还原、覆盖没还原的局部还原）：
    /// 不能当预提交清理，否则会把 App override 当成原始值重新备份，丢失安装前的旧覆盖。</summary>
    [Fact]
    public async Task PendingSentinelWithInstalledOverrides_BackupMissingAndNoRebackup()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        _fx.Registry["*msftedit"] = "builtin";
        Directory.CreateDirectory(_fx.BackupDir);
        File.WriteAllText(
            Path.Combine(_fx.BackupDir, DependencyPaths.BackupPendingFileName), "pending");
        // 模拟只还原了 DLL、覆盖仍是安装器写入的 native,builtin/native
        _fx.Registry["*msftedit"] = "native,builtin";
        _fx.Registry["*second"] = "native";

        var installer = _fx.CreateInstaller();
        Assert.Equal(
            DependencyStateKind.BackupMissing,
            installer.GetState(_fx.Target, _fx.Manifest).Kind);

        var ex = await Assert.ThrowsAsync<DependencyException>(
            () => installer.InstallAsync(_fx.Target, _fx.Manifest));

        Assert.Equal(DependencyFailureKind.BackupMissing, ex.Kind);
        Assert.True(Directory.Exists(_fx.BackupDir));
        Assert.Equal("native,builtin", _fx.Registry["*msftedit"]);
    }

    /// <summary>只还原了 `*msftedit`，`*second` 仍是 App 写入值：任一期望键仍匹配就不能当预提交，
    /// 否则重装备份会把 *second 的 App 值当成原始值。</summary>
    [Fact]
    public async Task PendingSentinelWithPartiallyRestoredOverrides_BackupMissing()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        _fx.Registry["*msftedit"] = "builtin";
        Directory.CreateDirectory(_fx.BackupDir);
        File.WriteAllText(
            Path.Combine(_fx.BackupDir, DependencyPaths.BackupPendingFileName), "pending");
        _fx.Registry["*msftedit"] = "builtin";
        _fx.Registry["*second"] = "native";

        var installer = _fx.CreateInstaller();
        Assert.Equal(
            DependencyStateKind.BackupMissing,
            installer.GetState(_fx.Target, _fx.Manifest).Kind);
        Assert.Equal(
            DependencyFailureKind.BackupMissing,
            (await Assert.ThrowsAsync<DependencyException>(
                () => installer.InstallAsync(_fx.Target, _fx.Manifest))).Kind);
        Assert.True(Directory.Exists(_fx.BackupDir));
        Assert.Equal("native", _fx.Registry["*second"]);
    }

    /// <summary>哨兵残留但当前已是原生 DLL/覆盖（如哨兵删除失败后又丢失 marker/manifest）：
    /// 必须按 BackupMissing 保留备份，不能删除/重装备份原生态。</summary>
    [Fact]
    public async Task PendingSentinelWithNativeDllsAndOverrides_BackupMissingAndNoRebackup()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        _fx.Registry["*msftedit"] = "builtin";
        await _fx.InstallAsync();
        var backupX64 = Path.Combine(_fx.BackupDir, "x64-msftedit.dll");
        var originalX64 = await File.ReadAllBytesAsync(backupX64);
        // 模拟哨兵删除失败：安装成功后仍留下 .incomplete，再丢失 marker/manifest
        await File.WriteAllTextAsync(
            Path.Combine(_fx.BackupDir, DependencyPaths.BackupPendingFileName), "pending");
        File.Delete(Path.Combine(_fx.StateDir, "test-native-dll.ok"));
        File.Delete(_fx.BackupManifestPath);

        var installer = _fx.CreateInstaller();
        Assert.Equal(
            DependencyStateKind.BackupMissing,
            installer.GetState(_fx.Target, _fx.Manifest).Kind);

        var ex = await Assert.ThrowsAsync<DependencyException>(
            () => installer.InstallAsync(_fx.Target, _fx.Manifest));

        Assert.Equal(DependencyFailureKind.BackupMissing, ex.Kind);
        Assert.Equal(_fx.X64Bytes, await File.ReadAllBytesAsync(_fx.System32Dll));
        Assert.Equal(originalX64, await File.ReadAllBytesAsync(backupX64));
    }

    /// <summary>哨兵残留、原生 DLL 未还原但覆盖被手动改回旧值（另一方向的局部还原）：
    /// 仍不能当预提交清理。</summary>
    [Fact]
    public async Task PendingSentinelWithNativeDllsButOldOverrides_BackupMissing()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        _fx.Registry["*msftedit"] = "builtin";
        await _fx.InstallAsync();
        await File.WriteAllTextAsync(
            Path.Combine(_fx.BackupDir, DependencyPaths.BackupPendingFileName), "pending");
        File.Delete(Path.Combine(_fx.StateDir, "test-native-dll.ok"));
        File.Delete(_fx.BackupManifestPath);
        _fx.Registry["*msftedit"] = "builtin";
        _fx.Registry.Remove("*second");

        var installer = _fx.CreateInstaller();
        Assert.Equal(
            DependencyStateKind.BackupMissing,
            installer.GetState(_fx.Target, _fx.Manifest).Kind);
        Assert.Equal(
            DependencyFailureKind.BackupMissing,
            (await Assert.ThrowsAsync<DependencyException>(
                () => installer.InstallAsync(_fx.Target, _fx.Manifest))).Kind);
        Assert.Equal(_fx.X64Bytes, await File.ReadAllBytesAsync(_fx.System32Dll));
    }

    /// <summary>哨兵在但覆盖查询不可用：不能猜“原状”，保守 BackupMissing。</summary>
    [Fact]
    public void PendingSentinelWithOverrideQueryUnavailable_BackupMissing()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        _fx.Registry["*msftedit"] = "builtin";
        Directory.CreateDirectory(_fx.BackupDir);
        File.WriteAllText(
            Path.Combine(_fx.BackupDir, DependencyPaths.BackupPendingFileName), "pending");
        _fx.FailOverrideQueryWithTimeout = true;

        Assert.Equal(
            DependencyStateKind.BackupMissing,
            _fx.CreateInstaller().GetState(_fx.Target, _fx.Manifest).Kind);
    }

    // ── 卸载：完整还原 ──────────────────────────────────────────────────────

    /// <summary>Proton 分支：卸载按「当前」Proton 树重建链接（升级后不能写死旧目录），删覆盖/marker/备份。</summary>
    [Fact]
    public async Task Uninstall_Proton_RestoresCurrentTreeLinksAndRemovesOverrideAndState()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("符号链接创建需要特权（Windows）");
            return;
        }

        _fx.InitPrefix(WinetricksFixture.OriginalKind.Symlink);
        await _fx.InstallAsync();

        // 模拟 Proton 升级：新树 + 新 wine 路径
        var (newWine, _, newLibX64, newLibX86) = _fx.CreateProtonTree("proton-new");
        _fx.WineExecutable = newWine;
        _fx.Wineserver = Path.Combine(Path.GetDirectoryName(newWine)!, "wineserver");

        await _fx.UninstallAsync();

        Assert.Equal(newLibX64, new FileInfo(_fx.System32Dll).LinkTarget);
        Assert.Equal(newLibX86, new FileInfo(_fx.SysWow64Dll).LinkTarget);
        Assert.DoesNotContain("*msftedit", _fx.Registry.Keys);
        Assert.False(File.Exists(Path.Combine(_fx.StateDir, "test-native-dll.ok")));
        Assert.False(Directory.Exists(_fx.BackupDir));
    }

    /// <summary>Proton 升级删除旧树后 builtin 链接悬空：卸载先修复（否则 reg 调用起不来），再还原。</summary>
    [Fact]
    public async Task Uninstall_DanglingBuiltinLinks_RepairedBeforeRestore()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("符号链接创建需要特权（Windows）");
            return;
        }

        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        await _fx.InstallAsync();

        var newKernel = Path.Combine(Path.GetDirectoryName(_fx.LibX64)!, "kernel32.dll");
        await File.WriteAllTextAsync(newKernel, "builtin kernel32");
        var danglingLink = Path.Combine(_fx.System32Dir, "kernel32.dll");
        File.CreateSymbolicLink(
            danglingLink,
            Path.Combine(_fx.Temp.FilePath("proton-old"), "files", "lib", "wine",
                "x86_64-windows", "kernel32.dll"));

        await _fx.UninstallAsync();

        Assert.Equal(newKernel, new FileInfo(danglingLink).LinkTarget);
        Assert.Equal("builtin x64", await File.ReadAllTextAsync(_fx.System32Dll));
    }

    /// <summary>回滚自身失败（DLL 目录被改成只读）：RollbackFailed，保留备份与原始失败信息。</summary>
    [Fact]
    public async Task Install_RollbackFails_ThrowsRollbackFailed_KeepsBackup()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("POSIX 目录权限语义");
            return;
        }

        if (Environment.IsPrivilegedProcess)
        {
            Assert.Skip("root 会绕过目录权限（无法构造回滚失败）");
            return;
        }

        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        _fx.ReadOnlyDllDirectoriesAfterWinetricks = true;
        _fx.FailOverride = true;
        try
        {
            var ex = await Assert.ThrowsAsync<DependencyException>(() => _fx.InstallAsync());

            Assert.Equal(DependencyFailureKind.RollbackFailed, ex.Kind);
            Assert.Contains("回滚失败", ex.Message);
            Assert.True(Directory.Exists(_fx.BackupDir), "回滚失败必须保留备份");
            Assert.True(
                Directory.Exists(_fx.BackupDir) && File.Exists(_fx.BackupManifestPath),
                "回滚失败必须保留备份清单");
            Assert.False(File.Exists(Path.Combine(_fx.StateDir, "test-native-dll.ok")));
        }
        finally
        {
            foreach (var directory in new[] { _fx.System32Dir, _fx.SysWow64Dir })
            {
                File.SetUnixFileMode(
                    directory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    /// <summary>被篡改/损坏的备份清单（越界路径、null 条目、缺 SHA-256）一律 BackupMissing：
    /// 不得把备份目录之外的路径当成可还原副本，也不得在加载阶段 NRE。</summary>
    [Fact]
    public async Task GetState_TamperedBackupManifest_BackupMissing_AndUninstallNoModification()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        await _fx.InstallAsync();
        var original = await File.ReadAllTextAsync(_fx.BackupManifestPath);

        // 1) backupFileName 用 ../ 指向备份目录之外（目标文件真实存在，只有路径规则能拒绝）
        await File.WriteAllBytesAsync(Path.Combine(_fx.StateDir, "evil.dll"), _fx.X64Bytes);
        var tamperedPath = original.Replace(
            "\"backupFileName\": \"x64-msftedit.dll\"",
            "\"backupFileName\": \"../evil.dll\"",
            StringComparison.Ordinal);
        Assert.NotEqual(original, tamperedPath);
        await File.WriteAllTextAsync(_fx.BackupManifestPath, tamperedPath);
        Assert.Equal(
            DependencyStateKind.BackupMissing,
            _fx.CreateInstaller().GetState(_fx.Target, _fx.Manifest).Kind);

        var ex = await Assert.ThrowsAsync<DependencyException>(() => _fx.UninstallAsync());
        Assert.Equal(DependencyFailureKind.BackupMissing, ex.Kind);
        Assert.Equal(_fx.X64Bytes, await File.ReadAllBytesAsync(_fx.System32Dll));
        Assert.True(File.Exists(Path.Combine(_fx.StateDir, "test-native-dll.ok")));

        // 2) dlls 数组里注入 null：加载器必须把它当"清单不可用"而不是 NRE
        var nullEntry = original.Replace(
            "\"dlls\": [", "\"dlls\": [\n      null,", StringComparison.Ordinal);
        Assert.NotEqual(original, nullEntry);
        await File.WriteAllTextAsync(_fx.BackupManifestPath, nullEntry);
        Assert.Equal(
            DependencyStateKind.BackupMissing,
            _fx.CreateInstaller().GetState(_fx.Target, _fx.Manifest).Kind);

        // 3) File 形态缺 SHA-256：不能跳过还原后的内容校验
        var x64Sha = Hashing.Sha256Hex(Path.Combine(_fx.BackupDir, "x64-msftedit.dll"));
        var missingSha = original.Replace(
            $"\"sha256\": \"{x64Sha}\"", "\"sha256\": null", StringComparison.Ordinal);
        Assert.NotEqual(original, missingSha);
        await File.WriteAllTextAsync(_fx.BackupManifestPath, missingSha);
        Assert.Equal(
            DependencyStateKind.BackupMissing,
            _fx.CreateInstaller().GetState(_fx.Target, _fx.Manifest).Kind);
    }

    /// <summary>损坏的备份清单：卸载按 BackupMissing 拒绝且零修改（不能当成可还原）。</summary>
    [Fact]
    public async Task Uninstall_CorruptBackup_BackupMissing_ZeroModification()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        await _fx.InstallAsync();
        await File.WriteAllTextAsync(_fx.BackupManifestPath, "{ bad json");

        var ex = await Assert.ThrowsAsync<DependencyException>(() => _fx.UninstallAsync());

        Assert.Equal(DependencyFailureKind.BackupMissing, ex.Kind);
        Assert.Equal(_fx.X64Bytes, await File.ReadAllBytesAsync(_fx.System32Dll));
        Assert.Equal("native,builtin", _fx.Registry["*msftedit"]);
        Assert.True(File.Exists(Path.Combine(_fx.StateDir, "test-native-dll.ok")));
    }

    /// <summary>多个旧覆盖值：安装写入新值，卸载全部恢复旧值（不只是 *msftedit）。</summary>
    [Fact]
    public async Task Uninstall_RestoresMultipleOldOverrideValues()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        _fx.Registry["*msftedit"] = "builtin";
        _fx.Registry["*second"] = "disabled";
        await _fx.InstallAsync();
        Assert.Equal("native,builtin", _fx.Registry["*msftedit"]);
        Assert.Equal("native", _fx.Registry["*second"]);

        await _fx.UninstallAsync();

        Assert.Equal("builtin", _fx.Registry["*msftedit"]);
        Assert.Equal("disabled", _fx.Registry["*second"]);
    }

    /// <summary>marker 缺失但备份有效：不自动还原（无法确认安装完成），零修改报 BackupMissing。</summary>
    [Fact]
    public async Task Uninstall_MarkerMissing_BackupMissing_ZeroModification()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        await _fx.InstallAsync();
        File.Delete(Path.Combine(_fx.StateDir, "test-native-dll.ok"));

        var ex = await Assert.ThrowsAsync<DependencyException>(() => _fx.UninstallAsync());

        Assert.Equal(DependencyFailureKind.BackupMissing, ex.Kind);
        Assert.Equal(_fx.X64Bytes, await File.ReadAllBytesAsync(_fx.System32Dll));
        Assert.True(Directory.Exists(_fx.BackupDir));
    }

    [Fact]
    public async Task Uninstall_RealFiles_RestoresBytes()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        await _fx.InstallAsync();

        await _fx.UninstallAsync();

        Assert.Equal("builtin x64", await File.ReadAllTextAsync(_fx.System32Dll));
        Assert.Equal("builtin x86", await File.ReadAllTextAsync(_fx.SysWow64Dll));
        Assert.False(Directory.Exists(_fx.BackupDir));
        Assert.False(File.Exists(Path.Combine(_fx.StateDir, "test-native-dll.ok")));
    }

    [Fact]
    public async Task Uninstall_MissingOriginals_DeletesInstalledFiles()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.Missing);
        await _fx.InstallAsync();

        await _fx.UninstallAsync();

        Assert.False(File.Exists(_fx.System32Dll));
        Assert.False(File.Exists(_fx.SysWow64Dll));
    }

    /// <summary>旧覆盖值必须恢复（不能一律删除）：备份 native → 卸载写回 native。</summary>
    [Fact]
    public async Task Uninstall_RestoresOldOverrideValue()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        _fx.Registry["*msftedit"] = "builtin";
        await _fx.InstallAsync();
        Assert.Equal("native,builtin", _fx.Registry["*msftedit"]);

        await _fx.UninstallAsync();

        Assert.Equal("builtin", _fx.Registry["*msftedit"]);
    }

    [Fact]
    public async Task Uninstall_BackupMissing_ZeroModification()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        await _fx.InstallAsync();
        Directory.Delete(_fx.BackupDir, recursive: true);

        var ex = await Assert.ThrowsAsync<DependencyException>(() => _fx.UninstallAsync());

        Assert.Equal(DependencyFailureKind.BackupMissing, ex.Kind);
        Assert.Equal(_fx.X64Bytes, await File.ReadAllBytesAsync(_fx.System32Dll));
        Assert.True(File.Exists(Path.Combine(_fx.StateDir, "test-native-dll.ok")));
        Assert.Equal("native,builtin", _fx.Registry["*msftedit"]);
    }

    /// <summary>当前 Proton 树缺 DLL 目标：RestoreFailed，保留 marker/备份，且绝不创建悬空链接。</summary>
    [Fact]
    public async Task Uninstall_CurrentProtonTargetMissing_RestoreFailed_KeepsStateNoDanglingLink()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("符号链接创建需要特权（Windows）");
            return;
        }

        _fx.InitPrefix(WinetricksFixture.OriginalKind.Symlink);
        await _fx.InstallAsync();
        var (newWine, _, newLibX64, _) = _fx.CreateProtonTree("proton-new");
        _fx.WineExecutable = newWine;
        _fx.Wineserver = Path.Combine(Path.GetDirectoryName(newWine)!, "wineserver");
        File.Delete(newLibX64);

        var ex = await Assert.ThrowsAsync<DependencyException>(() => _fx.UninstallAsync());

        Assert.Equal(DependencyFailureKind.RestoreFailed, ex.Kind);
        Assert.True(File.Exists(Path.Combine(_fx.StateDir, "test-native-dll.ok")));
        Assert.True(Directory.Exists(_fx.BackupDir));
        // 当前目标仍是真实原生 DLL（未被删除/改成悬空链接）
        Assert.Null(new FileInfo(_fx.System32Dll).LinkTarget);
        Assert.Equal(_fx.X64Bytes, await File.ReadAllBytesAsync(_fx.System32Dll));
    }

    /// <summary>系统 wine / 自定义 prefix：按备份记录的 LinkTarget 原样重建，不猜 Proton 树。</summary>
    [Fact]
    public async Task Uninstall_SystemWine_UsesRecordedLinkTarget()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("符号链接创建需要特权（Windows）");
            return;
        }

        var systemWine = _fx.CreateSystemWine();
        _fx.InitPrefix(WinetricksFixture.OriginalKind.Symlink, wineExecutable: systemWine);
        await _fx.InstallAsync();

        await _fx.UninstallAsync();

        Assert.Equal(_fx.LibX64, new FileInfo(_fx.System32Dll).LinkTarget);
        Assert.Equal(_fx.LibX86, new FileInfo(_fx.SysWow64Dll).LinkTarget);
    }

    [Fact]
    public async Task Uninstall_SystemWineTargetMissing_RestoreFailed()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("符号链接创建需要特权（Windows）");
            return;
        }

        var systemWine = _fx.CreateSystemWine();
        _fx.InitPrefix(WinetricksFixture.OriginalKind.Symlink, wineExecutable: systemWine);
        await _fx.InstallAsync();
        File.Delete(_fx.LibX64);

        var ex = await Assert.ThrowsAsync<DependencyException>(() => _fx.UninstallAsync());

        Assert.Equal(DependencyFailureKind.RestoreFailed, ex.Kind);
        Assert.True(Directory.Exists(_fx.BackupDir));
    }

    // ── winetricks 账本与收尾 ───────────────────────────────────────────────

    /// <summary>winetricks.log 只删除含该 verb 的行（grep -qw 语义），其他 verb 原样保留。</summary>
    [Fact]
    public async Task Uninstall_CleansWinetricksLedgerVerbOnly()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        await _fx.InstallAsync();
        var ledger = Path.Combine(_fx.Prefix, "winetricks.log");
        await File.WriteAllTextAsync(
            ledger,
            "testverb\nvcrun2022\nwinetricks -q testverb\ntestverb2024\n");

        await _fx.UninstallAsync();

        Assert.Equal(
            ["vcrun2022", "testverb2024"],
            await File.ReadAllLinesAsync(ledger));
    }

    /// <summary>账本只剩本 verb 的行：删除文件（安装前没有该文件，完整还原）。</summary>
    [Fact]
    public async Task Uninstall_CleansWinetricksLedger_DeletesFileWhenOnlyOurVerb()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        await _fx.InstallAsync();
        var ledger = Path.Combine(_fx.Prefix, "winetricks.log");
        await File.WriteAllTextAsync(ledger, "testverb\n");

        await _fx.UninstallAsync();

        Assert.False(File.Exists(ledger));
    }

    /// <summary>账本不可读：降级为警告，不阻断 DLL/覆盖还原与收尾。</summary>
    [Fact]
    public async Task Uninstall_LedgerCleanupFailure_WarnsButSucceeds()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("POSIX 文件权限语义");
            return;
        }

        if (Environment.IsPrivilegedProcess)
        {
            Assert.Skip("root 会绕过读写权限（无法构造不可读账本）");
            return;
        }

        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        await _fx.InstallAsync();
        var ledger = Path.Combine(_fx.Prefix, "winetricks.log");
        await File.WriteAllTextAsync(ledger, "testverb\n");
        var originalMode = File.GetUnixFileMode(ledger);
        File.SetUnixFileMode(ledger, UnixFileMode.None);
        try
        {
            await _fx.UninstallAsync();
        }
        finally
        {
            File.SetUnixFileMode(ledger, originalMode);
        }

        Assert.False(File.Exists(Path.Combine(_fx.StateDir, "test-native-dll.ok")));
        Assert.False(Directory.Exists(_fx.BackupDir));
        Assert.Equal("builtin x64", await File.ReadAllTextAsync(_fx.System32Dll));
    }

    /// <summary>收尾删除备份目录失败：仅警告并继续删 marker；残留目录让 GetState 报 BackupMissing
    /// （UI 文案提示可手动清理）。</summary>
    [Fact]
    public async Task Uninstall_BackupDeleteFailure_WarnsAndGetStateBackupMissing()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("POSIX 权限语义");
            return;
        }

        if (Environment.IsPrivilegedProcess)
        {
            Assert.Skip("root 会绕过目录权限（无法构造删除失败）");
            return;
        }

        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        await _fx.InstallAsync();
        var blocked = Path.Combine(_fx.BackupDir, "blocked");
        Directory.CreateDirectory(blocked);
        await File.WriteAllTextAsync(Path.Combine(blocked, "keep.txt"), "x");
        File.SetUnixFileMode(blocked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            await _fx.UninstallAsync(); // 不抛：删除失败仅告警
        }
        finally
        {
            File.SetUnixFileMode(blocked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // 递归删除可能先删掉 manifest 再撞上 blocked：残留目录 + marker → BackupMissing
        // （UI 文案提示可手动清理残留标记/目录）
        Assert.True(File.Exists(Path.Combine(_fx.StateDir, "test-native-dll.ok")));
        Assert.True(Directory.Exists(_fx.BackupDir));
        Assert.Equal(
            DependencyStateKind.BackupMissing,
            _fx.CreateInstaller().GetState(_fx.Target, _fx.Manifest).Kind);
    }

    // ── 备份失败分支（计划 §4/§7：键不存在不算失败，执行错误才失败）──────────

    [Fact]
    public async Task Install_DllOverridesKeyMissing_BacksUpNullOldValueAndInstalls()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        _fx.RegistryKeyExists = false;

        await _fx.InstallAsync();

        var backup = _fx.LoadBackup();
        Assert.All(backup.Overrides, entry => Assert.Null(entry.OldValue));
        Assert.Equal("native,builtin", _fx.Registry["*msftedit"]);
        Assert.Equal(
            DependencyStateKind.Installed,
            _fx.CreateInstaller().GetState(_fx.Target, _fx.Manifest).Kind);
    }

    /// <summary>已有旧覆盖时自动备份 dlloverrides.reg：manifest 丢失后 PITFALLS 的手动恢复/导入
    /// 依赖该文件。这里验证启动器产出的文件内容；PITFALLS 的 shell 步骤本身只做 `bash -n` 语法检查，
    /// 不在 xunit 内执行。</summary>
    [Fact]
    public async Task Install_WithExistingOverride_BacksUpDlloverridesRegForManualRestore()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        _fx.Registry["*msftedit"] = "builtin";

        await _fx.InstallAsync();

        var exportPath = Path.Combine(_fx.BackupDir, "dlloverrides.reg");
        Assert.True(File.Exists(exportPath));
        Assert.Contains("\"*msftedit\"=\"builtin\"", File.ReadAllText(exportPath));
        Assert.Equal(
            "builtin", _fx.LoadBackup().Overrides.Single(o => o.Key == "*msftedit").OldValue);
    }

    /// <summary>注册表值名大小写不敏感：`*MSFTEDIT=builtin` 必须被当作清单 `*msftedit` 的旧值，
    /// 备份/状态/卸载都不能因大小写丢失原值。</summary>
    [Fact]
    public async Task UppercaseRegistryValue_BackedUpComparedAndRestoredCaseInsensitively()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        _fx.Registry["*MSFTEDIT"] = "builtin";

        await _fx.InstallAsync();

        Assert.Equal(
            "builtin", _fx.LoadBackup().Overrides.Single(o => o.Key == "*msftedit").OldValue);
        Assert.Equal(
            DependencyStateKind.Installed,
            _fx.CreateInstaller().GetState(_fx.Target, _fx.Manifest).Kind);

        await _fx.UninstallAsync();

        Assert.Equal("builtin", _fx.Registry["*MSFTEDIT"]);
        Assert.False(Directory.Exists(_fx.BackupDir));
    }

    /// <summary>大小写变体旧覆盖不能被预提交判定误当成 App 值；此状态应可直接重装并保留旧值。</summary>
    [Fact]
    public async Task PendingSentinelWithUppercaseOldOverride_IsPreCommit()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        _fx.Registry["*MSFTEDIT"] = "builtin";
        Directory.CreateDirectory(_fx.BackupDir);
        await File.WriteAllTextAsync(
            Path.Combine(_fx.BackupDir, DependencyPaths.BackupPendingFileName), "pending");

        Assert.Equal(
            DependencyStateKind.NotInstalled,
            _fx.CreateInstaller().GetState(_fx.Target, _fx.Manifest).Kind);

        await _fx.InstallAsync();
        await _fx.UninstallAsync();

        Assert.Equal("builtin", _fx.Registry["*MSFTEDIT"]);
    }

    /// <summary>reg query 与父键探针都失败：注册表读取坏了，按 BackupFailed 拒绝且零写入。</summary>
    [Fact]
    public async Task Install_RegistryQueryAndProbeFail_BackupFailed_ZeroModification()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        _fx.RegistryKeyExists = false;
        _fx.WineBroken = true;

        var ex = await Assert.ThrowsAsync<DependencyException>(() => _fx.InstallAsync());

        Assert.Equal(DependencyFailureKind.BackupFailed, ex.Kind);
        Assert.False(Directory.Exists(_fx.BackupDir));
        Assert.False(File.Exists(Path.Combine(_fx.StateDir, "test-native-dll.ok")));
        Assert.Equal("builtin x64", await File.ReadAllTextAsync(_fx.System32Dll));
    }

    [Fact]
    public async Task Install_DllOverridesExportFails_BackupFailed_ZeroModification()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        _fx.FailExport = true;

        var ex = await Assert.ThrowsAsync<DependencyException>(() => _fx.InstallAsync());

        Assert.Equal(DependencyFailureKind.BackupFailed, ex.Kind);
        Assert.False(Directory.Exists(_fx.BackupDir));
        Assert.False(File.Exists(Path.Combine(_fx.StateDir, "test-native-dll.ok")));
        Assert.Equal("builtin x64", await File.ReadAllTextAsync(_fx.System32Dll));
    }

    // ── 并发 / 残留清理 ─────────────────────────────────────────────────────

    /// <summary>UninstallAsync 必须与安装共用 id 锁：安装取消后的回滚还在跑时，新页面的还原要等待。</summary>
    [Fact]
    public async Task Uninstall_WaitsForInFlightInstall_ThenRestores()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        using var gate = new ManualResetEventSlim(false);
        _fx.WinetricksGate = gate;

        var install = Task.Run(() => _fx.InstallAsync());
        for (var i = 0; i < 500 && !_fx.WinetricksEntered; i++)
        {
            await Task.Delay(10);
        }

        Assert.True(_fx.WinetricksEntered, "安装未走到 winetricks 阶段");
        var uninstall = Task.Run(() => _fx.UninstallAsync());
        await Task.Delay(150);
        Assert.False(
            uninstall.IsCompleted, "UninstallAsync 未取安装锁，与在途安装并发修改 prefix");

        gate.Set();
        await install.WaitAsync(TimeSpan.FromSeconds(10));
        await uninstall.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(
            DependencyStateKind.NotInstalled,
            _fx.CreateInstaller().GetState(_fx.Target, _fx.Manifest).Kind);
        Assert.Equal("builtin x64", await File.ReadAllTextAsync(_fx.System32Dll));
    }

    /// <summary>安全清理场景：DLL/覆盖已还原、只剩 marker + 备份目录（清单已随删除失败丢失）。
    /// 按 UI 指引删两处残留后回到 NotInstalled；重装→再卸载必须仍恢复原始真实文件与无覆盖状态，
    /// 证明清理没有把唯一原始副本当成“新原始态”重新备份。</summary>
    [Fact]
    public async Task BackupMissingResidue_AfterRestore_ManualCleanupAndReinstallPreservesOriginals()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        await _fx.InstallAsync();

        // 模拟“还原已成功、备份目录删除失败”的安全残留：DLL/覆盖已恢复安装前状态，清单丢失
        File.Copy(Path.Combine(_fx.BackupDir, "x64-msftedit.dll"), _fx.System32Dll, overwrite: true);
        File.Copy(Path.Combine(_fx.BackupDir, "x86-msftedit.dll"), _fx.SysWow64Dll, overwrite: true);
        _fx.Registry.Remove("*msftedit");
        _fx.Registry.Remove("*second");
        File.Delete(_fx.BackupManifestPath);

        Assert.Equal(
            DependencyStateKind.BackupMissing,
            _fx.CreateInstaller().GetState(_fx.Target, _fx.Manifest).Kind);

        // UI 指引的清理步骤（仅在已还原前提成立时）：删 marker 与残留备份目录
        File.Delete(Path.Combine(_fx.StateDir, "test-native-dll.ok"));
        Directory.Delete(_fx.BackupDir, recursive: true);
        Assert.Equal(
            DependencyStateKind.NotInstalled,
            _fx.CreateInstaller().GetState(_fx.Target, _fx.Manifest).Kind);

        // 清理后重装，再卸载：必须恢复 builtin 原始文件与“原本无覆盖”状态
        await _fx.InstallAsync();
        Assert.Equal(
            DependencyStateKind.Installed,
            _fx.CreateInstaller().GetState(_fx.Target, _fx.Manifest).Kind);

        await _fx.UninstallAsync();
        Assert.Equal("builtin x64", await File.ReadAllTextAsync(_fx.System32Dll));
        Assert.Equal("builtin x86", await File.ReadAllTextAsync(_fx.SysWow64Dll));
        Assert.DoesNotContain("*msftedit", _fx.Registry.Keys);
        Assert.DoesNotContain("*second", _fx.Registry.Keys);
        Assert.False(Directory.Exists(_fx.BackupDir));
    }

    // ── 字体依赖 no-op ──────────────────────────────────────────────────────

    [Fact]
    public async Task Uninstall_FontDependency_NoOp()
    {
        _fx.InitPrefix(WinetricksFixture.OriginalKind.RealFile);
        var manifest = new DependencyManifest(
            "test-fonts", "1.0",
            "https://example.com/fonts.zip", "fonts.zip",
            "ce3fc169af61a4e7bc650e7e78186330", 1,
            [new DependencyFont("a.ttc", ["F"])],
            [new DependencyReplacementGroup("F", ["Old"])]);

        await _fx.CreateInstaller().UninstallAsync(_fx.Target, manifest);

        Assert.Empty(_fx.Runner.Specs);
        Assert.False(Directory.Exists(_fx.BackupDir));
    }

    /// <summary>
    /// 假 Wine 世界的夹具：真实临时文件系统 + 按 argv 分派的假进程（wineserver/winetricks/wine reg）。
    /// </summary>
    private sealed class WinetricksFixture : IDisposable
    {
        public enum OriginalKind
        {
            RealFile,
            Symlink,
            Missing,
        }

        private readonly TempDir _temp = new();

        public WinetricksFixture()
        {
            X64Bytes = "native-x64"u8.ToArray();
            X86Bytes = "native-x86"u8.ToArray();
            var (wine, wineserver, libX64, libX86) = CreateProtonTree("proton-11.0-14");
            WineExecutable = wine;
            Wineserver = wineserver;
            LibX64 = libX64;
            LibX86 = libX86;
            var bundledWinetricks = Path.Combine(
                Temp.FilePath("proton-11.0-14"), "protonfixes", "files", "bin", "winetricks");
            WinetricksPath = bundledWinetricks;
            Directory.CreateDirectory(Path.GetDirectoryName(bundledWinetricks)!);
            WriteExecutable(bundledWinetricks);
            Manifest = new DependencyManifest(
                "test-native-dll", "1.0",
                null, null, null, null, null, null,
                WinetricksVerb: "testverb",
                Dlls:
                [
                    new DependencyDll("x64", "msftedit.dll", X64Bytes.Length, Hashing.Sha256Hex(X64Bytes)),
                    new DependencyDll("x86", "msftedit.dll", X86Bytes.Length, Hashing.Sha256Hex(X86Bytes)),
                ],
                DllOverrides: new Dictionary<string, string>
                {
                    ["*msftedit"] = "native,builtin",
                    ["*second"] = "native",
                });
            Runner.Handler = Handle;
        }

        public TempDir Temp => _temp;

        public FakeProcessRunner Runner { get; } = new();

        public DependencyManifest Manifest { get; }

        public byte[] X64Bytes { get; }

        public byte[] X86Bytes { get; }

        public string WineExecutable { get; set; }

        public string Wineserver { get; set; }

        public string? WinetricksPath { get; set; }

        public string LibX64 { get; }

        public string LibX86 { get; }

        public string Prefix => Temp.FilePath("prefix");

        public string StateDir => Path.Combine(Prefix, ".yagl-deps");

        public string BackupDir => Path.Combine(StateDir, "test-native-dll-backup");

        public string BackupManifestPath => Path.Combine(BackupDir, "manifest.json");

        public string SystemRegPath => Path.Combine(Prefix, "system.reg");

        public string System32Dir => Path.Combine(Prefix, "drive_c", "windows", "system32");

        public string SysWow64Dir => Path.Combine(Prefix, "drive_c", "windows", "syswow64");

        public string System32Dll => Path.Combine(System32Dir, "msftedit.dll");

        public string SysWow64Dll => Path.Combine(SysWow64Dir, "msftedit.dll");

        /// <summary>假注册表：值名大小写不敏感（与 Wine/Windows 一致），值数据仍原样保存。</summary>
        public Dictionary<string, string> Registry { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool RegistryKeyExists { get; set; } = true;

        public bool WineBroken { get; set; }

        /// <summary>DllOverrides 查询抛超时（模拟冷 Wine/慢盘；GetState 不能误报 Damaged）。</summary>
        public bool FailOverrideQueryWithTimeout { get; set; }

        public bool Busy { get; set; }

        public bool FailWinetricks { get; set; }

        public bool CancelWinetricks { get; set; }

        public bool FailOverride { get; set; }

        public string? FailOverrideOnValue { get; set; }

        public bool CorruptWinetricksDll { get; set; }

        /// <summary>reg export 失败（备份导出安全性测试）。</summary>
        public bool FailExport { get; set; }

        /// <summary>winetricks 执行时的闸门（并发串行测试用）；null = 直接跑。</summary>
        public ManualResetEventSlim? WinetricksGate { get; set; }

        /// <summary>winetricks 已进入（并发测试轮询用）。</summary>
        public bool WinetricksEntered { get; private set; }

        /// <summary>winetricks 拷入 DLL 后把两个系统目录改成只读（构造回滚失败：删除/复制都写不进去）。</summary>
        public bool ReadOnlyDllDirectoriesAfterWinetricks { get; set; }

        public string[]? WinetricksArgs { get; private set; }

        public IReadOnlyDictionary<string, string>? WinetricksEnvironment { get; private set; }

        public bool MarkerSeenDuringWinetricks { get; private set; }

        public int WinetricksCallCount { get; private set; }

        public WinePrefixTarget Target => new(
            WineExecutable,
            Prefix,
            Path.Combine(Prefix, "drive_c", "windows", "Fonts"),
            StateDir);

        /// <summary>建 Proton 树（wine/wineserver + 两份 builtin msftedit）；返回路径与当前链接目标。</summary>
        public (string Wine, string Wineserver, string LibX64, string LibX86) CreateProtonTree(string name)
        {
            var root = Temp.FilePath(name);
            var wine = Path.Combine(root, "files", "bin", "wine");
            var wineserver = Path.Combine(root, "files", "bin", "wineserver");
            WriteExecutable(wine);
            WriteExecutable(wineserver);
            var libX64 = Path.Combine(root, "files", "lib", "wine", "x86_64-windows", "msftedit.dll");
            var libX86 = Path.Combine(root, "files", "lib", "wine", "i386-windows", "msftedit.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(libX64)!);
            Directory.CreateDirectory(Path.GetDirectoryName(libX86)!);
            File.WriteAllText(libX64, "builtin x64");
            File.WriteAllText(libX86, "builtin x86");
            return (wine, wineserver, libX64, libX86);
        }

        /// <summary>建系统 wine（非 files/bin 形态，无法推导 Proton 根）；wineserver 与 wine 同目录。</summary>
        public string CreateSystemWine()
        {
            var wine = Temp.FilePath("system-wine", "bin", "wine");
            WriteExecutable(wine);
            WriteExecutable(Path.Combine(Path.GetDirectoryName(wine)!, "wineserver"));
            return wine;
        }

        public void InitPrefix(
            OriginalKind originals,
            string? arch = "#arch=win64",
            string? wineExecutable = null)
        {
            if (wineExecutable is not null)
            {
                WineExecutable = wineExecutable;
                // 系统 wine 分支的 wineserver 解析：同目录优先
                Wineserver = Path.Combine(Path.GetDirectoryName(wineExecutable)!, "wineserver");
                WinetricksPath = Path.Combine(Temp.FilePath("system-wine", "bin", "winetricks"));
                WriteExecutable(WinetricksPath);
            }

            Directory.CreateDirectory(System32Dir);
            Directory.CreateDirectory(SysWow64Dir);
            Directory.CreateDirectory(StateDir);
            File.WriteAllText(
                SystemRegPath,
                "WINE REGISTRY Version 2\n;; All keys relative to REGISTRY\\\\Machine\n\n"
                + (arch is null ? "" : arch + "\n"));

            if (originals == OriginalKind.RealFile)
            {
                File.WriteAllText(System32Dll, "builtin x64");
                File.WriteAllText(SysWow64Dll, "builtin x86");
            }
            else if (originals == OriginalKind.Symlink)
            {
                File.CreateSymbolicLink(System32Dll, LibX64);
                File.CreateSymbolicLink(SysWow64Dll, LibX86);
            }
        }

        public DependencyInstaller CreateInstaller() =>
            new(new FakeDownloader(),
                Runner,
                dependencies: [Manifest],
                cacheRoot: Temp.FilePath("cache"),
                winetricksResolver: _ => WinetricksPath);

        public Task InstallAsync(CancellationToken cancellationToken = default) =>
            CreateInstaller().InstallAsync(Target, Manifest, null, cancellationToken);

        public Task UninstallAsync(CancellationToken cancellationToken = default) =>
            CreateInstaller().UninstallAsync(Target, Manifest, cancellationToken);

        public DependencyBackupManifest LoadBackup() =>
            System.Text.Json.JsonSerializer.Deserialize<DependencyBackupManifest>(
                File.ReadAllText(BackupManifestPath),
                Json.Default)!;

        public void Dispose() => _temp.Dispose();

        private static void WriteExecutable(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "#!/bin/sh\n");
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        private ProcessResult Handle(ProcessStartSpec spec)
        {
            var file = Path.GetFileName(spec.FileName);
            if (file == "wineserver")
            {
                if (Busy)
                {
                    throw new UpdateException("wineserver -w timed out");
                }

                return new ProcessResult(0, "", "");
            }

            if (file == "winetricks")
            {
                WinetricksCallCount++;
                WinetricksArgs = spec.ArgumentList?.ToArray();
                WinetricksEnvironment = spec.Environment;
                WinetricksEntered = true;
                WinetricksGate?.Wait(TimeSpan.FromSeconds(10));
                MarkerSeenDuringWinetricks |=
                    File.Exists(Path.Combine(StateDir, "test-native-dll.ok"));
                if (CancelWinetricks)
                {
                    throw new OperationCanceledException("cancel during winetricks");
                }

                if (FailWinetricks)
                {
                    return new ProcessResult(1, "download failed\nlast line\n", "download failed");
                }

                WriteInstalledDlls();
                return new ProcessResult(0, "winetricks ok", "");
            }

            if (file is "wine" or "wine64")
            {
                var args = spec.ArgumentList?.ToArray()
                           ?? throw new InvalidOperationException(
                               $"wine 调用必须使用 ArgumentList：{spec.Arguments}");
                return HandleWine(args);
            }

            throw new InvalidOperationException($"意外的进程调用：{spec.FileName} {spec.Arguments}");
        }

        private ProcessResult HandleWine(string[] args)
        {
            if (args.Length < 3 || args[0] != "reg")
            {
                throw new InvalidOperationException($"意外的 wine argv：{string.Join(' ', args)}");
            }

            var key = args[2];
            switch (args[1])
            {
                case "query":
                    return Query(key);
                case "export":
                    if (FailExport)
                    {
                        return new ProcessResult(1, "", "export failed");
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(args[3])!);
                    var reg = new StringBuilder(
                        "Windows Registry Editor Version 5.00\n\n"
                        + "[HKEY_CURRENT_USER\\Software\\Wine\\DllOverrides]\n");
                    foreach (var (name, value) in Registry)
                    {
                        reg.Append('"').Append(name).Append("\"=\"").Append(value).Append("\"\n");
                    }

                    File.WriteAllText(args[3], reg.ToString(), Encoding.Unicode);
                    return new ProcessResult(0, "", "");
                case "add":
                    {
                        var name = ValueName(args);
                        var value = args[Array.IndexOf(args, "/d") + 1];
                        if (FailOverride || (FailOverrideOnValue is not null && value == FailOverrideOnValue))
                        {
                            return new ProcessResult(1, "", "cannot write");
                        }

                        Registry[name] = value;
                        RegistryKeyExists = true;
                        return new ProcessResult(0, "", "");
                    }
                case "delete":
                    {
                        var name = ValueName(args);
                        if (FailOverride)
                        {
                            return new ProcessResult(1, "", "cannot delete");
                        }

                        return Registry.Remove(name)
                            ? new ProcessResult(0, "", "")
                            : new ProcessResult(1, "", "value not found");
                    }
                default:
                    throw new InvalidOperationException($"意外的 reg 子命令：{args[1]}");
            }
        }

        private static string ValueName(string[] args)
        {
            var index = Array.IndexOf(args, "/v");
            return args[index + 1];
        }

        private ProcessResult Query(string key)
        {
            if (WineBroken)
            {
                return new ProcessResult(53, "", "wine: failed to load ntdll.so");
            }

            const string OverridesKey = @"HKCU\Software\Wine\DllOverrides";
            if (key == OverridesKey)
            {
                if (FailOverrideQueryWithTimeout)
                {
                    throw new UpdateException("Process timed out after 3000 ms");
                }

                if (!RegistryKeyExists)
                {
                    return new ProcessResult(1, "", "not found");
                }

                var builder = new StringBuilder();
                builder.Append("\nHKEY_CURRENT_USER\\Software\\Wine\\DllOverrides\n");
                foreach (var (name, value) in Registry)
                {
                    builder.Append("    ").Append(name).Append("    REG_SZ    ").Append(value).Append('\n');
                }

                return new ProcessResult(0, builder.ToString(), "");
            }

            if (key == @"HKCU\Software\Wine")
            {
                return WineBroken
                    ? new ProcessResult(1, "", "wine is broken")
                    : new ProcessResult(0, "\nHKEY_CURRENT_USER\\Software\\Wine\n", "");
            }

            return new ProcessResult(1, "", "not found");
        }

        private void WriteInstalledDlls()
        {
            WriteDll(System32Dll, CorruptWinetricksDll ? [0x00] : X64Bytes);
            WriteDll(SysWow64Dll, CorruptWinetricksDll ? [0x00] : X86Bytes);
            if (ReadOnlyDllDirectoriesAfterWinetricks && OperatingSystem.IsLinux())
            {
                foreach (var directory in new[] { System32Dir, SysWow64Dir })
                {
                    File.SetUnixFileMode(
                        directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                }
            }
        }

        private static void WriteDll(string path, byte[] bytes)
        {
            if (File.Exists(path) || new FileInfo(path).LinkTarget is not null)
            {
                File.Delete(path); // 符号链接先删，绝不写穿
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
    }
}
