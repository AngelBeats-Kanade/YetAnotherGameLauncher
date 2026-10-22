using Xunit;
using YetAnotherGameLauncher.Core.Services.Umu;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Core.Tests.Services.Umu;

/// <summary>
/// 诱饵父进程桥准备逻辑（2026-10-18）：候选挑选顺序、诱饵复制、helper 落盘与失败回退。
/// 机制与真机实测矩阵见 artifacts/bugs.md「1.6.4 客户端拒绝非官方启动器」与
/// docs/ARCHITECTURE.md §3.1；helper 源码/构建/自检见 tools/wine-parent-bridge/。
/// 桥工作目录自 2026-10-20 起改为应用数据目录（游戏安装目录只读）。
/// </summary>
public sealed class DecoyParentBridgeTests : IDisposable
{
    private readonly TempDir _temp = new();

    /// <summary>测试专用桥根目录（生产为 AppPaths.DataDirectory/bridge/&lt;gameId&gt;）。</summary>
    private string BridgeRoot => _temp.FilePath("bridge-root");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ToWindowsPath_MapsLinuxAbsolutePathToZDrive()
    {
        // 纯字符串逻辑：wine 的 Z: 盘映射根为 /，桥只在此形态下使用（Linux）
        Assert.Equal(@"Z:\home\kanade\Games\Arknights Endfield", DecoyParentBridge.ToWindowsPath(
            "/home/kanade/Games/Arknights Endfield"));
        Assert.Equal(@"Z:\", DecoyParentBridge.ToWindowsPath("/"));
    }

    [Fact]
    public void TryPrepare_PicksFirstExistingCandidateInOrder()
    {
        var install = _temp.FilePath("game");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "PlatformProcess.exe"), "first-candidate");
        File.WriteAllText(Path.Combine(install, "UnityCrashHandler64.exe"), "second-candidate");
        File.WriteAllText(Path.Combine(install, "Endfield.exe"), "third-candidate");

        var prep = new DecoyParentBridge().TryPrepare(install, BridgeRoot);

        Assert.NotNull(prep);
        Assert.Equal(BridgeRoot, prep.BridgeDirectory);
        Assert.Equal(Path.Combine(BridgeRoot, "Games.exe"), prep.DecoyPath);
        Assert.Equal(Path.Combine(BridgeRoot, "yagl-parent-bridge.exe"), prep.HelperPath);
        Assert.Equal("first-candidate", File.ReadAllText(prep.DecoyPath));
        Assert.Equal(
            DecoyParentBridge.ToWindowsPath(prep.DecoyPath), prep.DecoyWinePath);
    }

    [Fact]
    public void TryPrepare_FallsToNextCandidateWhenFirstMissing()
    {
        var install = _temp.FilePath("game");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "UnityCrashHandler64.exe"), "unity-signed");
        File.WriteAllText(Path.Combine(install, "Endfield.exe"), "game-body");

        var prep = new DecoyParentBridge().TryPrepare(install, BridgeRoot);

        Assert.NotNull(prep);
        Assert.Equal("unity-signed", File.ReadAllText(prep.DecoyPath));
    }

    [Fact]
    public void TryPrepare_OverwritesStaleDecoyAndHelper()
    {
        // 上次退出被强杀会留下旧诱饵/旧 helper（helper 跨版本升级也要刷新）

        var install = _temp.FilePath("game");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(BridgeRoot);
        File.WriteAllText(Path.Combine(install, "PlatformProcess.exe"), "fresh-candidate");
        File.WriteAllText(Path.Combine(BridgeRoot, "Games.exe"), "stale-decoy");
        File.WriteAllText(Path.Combine(BridgeRoot, "yagl-parent-bridge.exe"), "stale-helper");

        var prep = new DecoyParentBridge().TryPrepare(install, BridgeRoot);

        Assert.NotNull(prep);
        Assert.Equal("fresh-candidate", File.ReadAllText(prep.DecoyPath));
        Assert.Equal(DecoyParentBridge.ReadEmbeddedHelper(), File.ReadAllBytes(prep.HelperPath));
    }

    [Fact]
    public void TryPrepare_KeepsMatchingDecoyWhenDestinationIsLocked()
    {
        // 2026-10-20 复审：Windows 上已挂起的诱饵进程锁住 Games.exe 镜像文件。
        // 内容与候选一致时必须跳过覆盖——第二次启动 / helper 被强杀留下孤儿诱饵仍可继续。
        // 用「目标不可写」模拟锁：Windows 持 FileShare.Read 句柄，Unix 去掉写权限。
        var install = _temp.FilePath("game");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(BridgeRoot);
        File.WriteAllText(Path.Combine(install, "PlatformProcess.exe"), "same-candidate");
        var decoy = Path.Combine(BridgeRoot, "Games.exe");
        File.WriteAllText(decoy, "same-candidate");

        if (!OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess)
        {
            // root 可绕过只读权限写入，无法用权限模拟文件锁；Windows 分支用共享句柄，不受影响
            Assert.Skip("特权进程下只读权限不能模拟文件锁");
        }

        if (OperatingSystem.IsWindows())
        {
            using var hold = new FileStream(decoy, FileMode.Open, FileAccess.Read, FileShare.Read);
            Assert.NotNull(new DecoyParentBridge().TryPrepare(install, BridgeRoot));
        }
        else
        {
            File.SetUnixFileMode(decoy, UnixFileMode.UserRead);
            try
            {
                Assert.NotNull(new DecoyParentBridge().TryPrepare(install, BridgeRoot));
            }
            finally
            {
                File.SetUnixFileMode(decoy, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
    }

    [Fact]
    public void TryPrepare_RemovesStaleStatusFilesButKeepsFreshOnes()
    {
        // 状态文件由 launcher 正常清理；崩溃/取消可能留下孤儿。按 1 天阈值清理，
        // 活跃会话（最多 20 s）不会被误删。
        var install = _temp.FilePath("game");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "PlatformProcess.exe"), "signed-decoy");
        Directory.CreateDirectory(BridgeRoot);
        var stale = Path.Combine(BridgeRoot, "status-stale.txt");
        var staleTemp = Path.Combine(BridgeRoot, "status-stale.txt.tmp");
        var fresh = Path.Combine(BridgeRoot, "status-fresh.txt");
        File.WriteAllText(stale, "STARTING");
        File.WriteAllText(staleTemp, "STARTING");
        File.WriteAllText(fresh, "STARTING");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(staleTemp, DateTime.UtcNow.AddDays(-2));

        var prep = new DecoyParentBridge().TryPrepare(install, BridgeRoot);

        Assert.NotNull(prep);
        Assert.False(File.Exists(stale));
        Assert.False(File.Exists(staleTemp));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void TryPrepare_NoCandidate_ReturnsNullWithoutCreatingBridgeDirectory()
    {
        var install = _temp.FilePath("game");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "EndfieldBase.dll"), "not-a-candidate");

        var prep = new DecoyParentBridge().TryPrepare(install, BridgeRoot);

        Assert.Null(prep);
        Assert.False(Directory.Exists(BridgeRoot));
        Assert.False(Directory.Exists(Path.Combine(install, ".yagl")));
    }

    [Fact]
    public void TryPrepare_MissingInstallDirectory_ReturnsNull()
    {
        Assert.Null(new DecoyParentBridge().TryPrepare(_temp.FilePath("does-not-exist"), BridgeRoot));
    }

    [Fact]
    public void TryPrepare_BridgePathBlockedByFile_ReturnsNull()
    {
        // 桥根目录被同名的普通文件占住（或权限异常）：准备失败不抛，回退直启

        var install = _temp.FilePath("game");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "PlatformProcess.exe"), "decoy");
        File.WriteAllText(BridgeRoot, "blocker");

        Assert.Null(new DecoyParentBridge().TryPrepare(install, BridgeRoot));
    }

    [Fact]
    public void TryPrepare_DoesNotWriteToInstallDirectory()
    {
        // 桥工作目录迁移到应用数据目录后，游戏安装目录只作为诱饵来源被读取

        var install = _temp.FilePath("game");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "PlatformProcess.exe"), "signed-decoy");

        var prep = new DecoyParentBridge().TryPrepare(install, BridgeRoot);

        Assert.NotNull(prep);
        Assert.False(Directory.Exists(Path.Combine(install, ".yagl")));
    }

    [Fact]
    public void TryPrepare_RemovesLegacyBridgeDirectory()
    {
        // 旧版把诱饵/helper 落在 <install>/.yagl/bridge；迁移后 best-effort 清掉，
        // 但不得触碰 .yagl 下的其它状态（state.json 等）

        var install = _temp.FilePath("game");
        Directory.CreateDirectory(install);
        var legacy = Path.Combine(install, ".yagl", "bridge");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "Games.exe"), "stale-decoy");
        File.WriteAllText(Path.Combine(install, ".yagl", "state.json"), "{}");
        File.WriteAllText(Path.Combine(install, "PlatformProcess.exe"), "signed-decoy");

        var prep = new DecoyParentBridge().TryPrepare(install, BridgeRoot);

        Assert.NotNull(prep);
        Assert.False(Directory.Exists(legacy));
        Assert.True(File.Exists(Path.Combine(install, ".yagl", "state.json")));
    }

    [Fact]
    public void BridgeSource_MatchesManagedBridgeContract()
    {
        // C#↔C# 契约锚点：helper 的环境变量名/退出码是独立项目里的常量，单方改名在其它
        // 程序集里不可见（失败形态是 helper 退出 2、游戏不出现而 UI 报「已启动」），
        // 故直接读 helper 源码锚定（2026-10-20 起 helper 由 C 改为 C#）。
        var path = FindBridgeSourcePath();
        Assert.True(File.Exists(path), $"找不到 helper 契约源码：{path}");
        var source = File.ReadAllText(path);

        foreach (var variable in new[]
                 {
                     DecoyParentBridge.DecoyVariable,
                     DecoyParentBridge.TargetVariable,
                     DecoyParentBridge.CommandLineVariable,
                     DecoyParentBridge.WorkingDirectoryVariable,
                     DecoyParentBridge.StatusVariable,
                 })
        {
            Assert.Contains($"\"{variable}\"", source, StringComparison.Ordinal);
        }

        // 状态值契约：READY = 游戏进程已创建；ERROR: = 桥失败（launcher 不再看退出码判桥故障）
        Assert.Contains("ReadyStatus = \"READY\"", source, StringComparison.Ordinal);
        Assert.Contains("ErrorStatusPrefix = \"ERROR:\"", source, StringComparison.Ordinal);

        // 退出码契约：2 = 必填输入缺失；3 = 诱饵创建/属性设置失败；4 = 游戏创建失败
        Assert.Contains("MissingInputExitCode = 2", source, StringComparison.Ordinal);
        Assert.Contains("DecoyFailureExitCode = 3", source, StringComparison.Ordinal);
        Assert.Contains("GameCreateFailureExitCode = 4", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedHelper_IsPinnedPeBinary()
    {
        // 嵌入 helper 的完整性锚点：自包含 .NET 单文件发布不保证跨机逐字节一致，
        // 因此不再钉 SHA-256，只钉 PE 结构与 GUI 子系统（console 子系统会在 Wine
        // 无终端时开 conhost 黑窗——2026-10-20 真机复现的根因）。
        var bytes = DecoyParentBridge.ReadEmbeddedHelper();

        Assert.True(bytes.Length > 0, "嵌入式 helper 不应为空");
        Assert.Equal((ushort)0x5A4D, BitConverter.ToUInt16(bytes, 0)); // 'MZ'
        var peOffset = BitConverter.ToInt32(bytes, 0x3C);
        Assert.Equal(0x00004550u, BitConverter.ToUInt32(bytes, peOffset)); // 'PE\0\0'
        Assert.Equal(
            (ushort)0x8664,
            BitConverter.ToUInt16(bytes, peOffset + 4)); // PE32+ x86-64

        var optionalHeader = peOffset + 4 + 20;
        Assert.Equal((ushort)0x20B, BitConverter.ToUInt16(bytes, optionalHeader)); // PE32+
        Assert.Equal(
            (ushort)2,
            BitConverter.ToUInt16(bytes, optionalHeader + 68)); // IMAGE_SUBSYSTEM_WINDOWS_GUI
    }

    /// <summary>定位仓库内 tools/wine-parent-bridge/BridgeContract.cs（从测试 bin 目录向上找；找不到 = 运行环境异常）。</summary>
    private static string FindBridgeSourcePath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tools", "wine-parent-bridge", "BridgeContract.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            "找不到 tools/wine-parent-bridge/BridgeContract.cs（测试需在仓库内运行）");
    }
}
