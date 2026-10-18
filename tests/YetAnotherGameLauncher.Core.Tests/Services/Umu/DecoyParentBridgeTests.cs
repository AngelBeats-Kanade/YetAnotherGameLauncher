using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Xunit;
using YetAnotherGameLauncher.Core.Services.Umu;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Core.Tests.Services.Umu;

/// <summary>
/// 诱饵父进程桥准备逻辑（2026-10-18）：候选挑选顺序、诱饵复制、helper 落盘与失败回退。
/// 机制与真机实测矩阵见 artifacts/bugs.md「1.6.4 客户端拒绝非官方启动器」与
/// docs/ARCHITECTURE.md §3.1；helper 源码/构建/自检见 tools/wine-parent-bridge/。
/// </summary>
public sealed class DecoyParentBridgeTests : IDisposable
{
    /// <summary>嵌入 helper 的 SHA-256 锚点——更新 tools/wine-parent-bridge/bridge.c 并重新构建后必须同步
    /// （build.sh 带 --no-insert-timestamp：同一源码重建逐字节一致，哈希可核对来源）。</summary>
    private const string EmbeddedHelperSha256 =
        "0c12ec751226aba75c2cd5fb5d92e90644a1d38c1f63df3c3d387c17a483b99e";

    private readonly TempDir _temp = new();

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
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("桥仅 Linux 原生 umu 链使用（路径形态为 Linux 绝对路径）");
        }

        var install = _temp.FilePath("game");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "PlatformProcess.exe"), "first-candidate");
        File.WriteAllText(Path.Combine(install, "UnityCrashHandler64.exe"), "second-candidate");
        File.WriteAllText(Path.Combine(install, "Endfield.exe"), "third-candidate");

        var prep = new DecoyParentBridge().TryPrepare(install);

        Assert.NotNull(prep);
        Assert.Equal(Path.Combine(install, ".yagl", "bridge"), prep.BridgeDirectory);
        Assert.Equal(Path.Combine(install, ".yagl", "bridge", "Games.exe"), prep.DecoyPath);
        Assert.Equal(Path.Combine(install, ".yagl", "bridge", "yagl-parent-bridge.exe"), prep.HelperPath);
        Assert.Equal("first-candidate", File.ReadAllText(prep.DecoyPath));
        Assert.Equal(
            DecoyParentBridge.ToWindowsPath(prep.DecoyPath), prep.DecoyWindowsPath);
    }

    [Fact]
    public void TryPrepare_FallsToNextCandidateWhenFirstMissing()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("桥仅 Linux 原生 umu 链使用");
        }

        var install = _temp.FilePath("game");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "UnityCrashHandler64.exe"), "unity-signed");
        File.WriteAllText(Path.Combine(install, "Endfield.exe"), "game-body");

        var prep = new DecoyParentBridge().TryPrepare(install);

        Assert.NotNull(prep);
        Assert.Equal("unity-signed", File.ReadAllText(prep.DecoyPath));
    }

    [Fact]
    public void TryPrepare_OverwritesStaleDecoyAndHelper()
    {
        // 上次退出被强杀会留下旧诱饵/旧 helper（helper 跨版本升级也要刷新）
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("桥仅 Linux 原生 umu 链使用");
        }

        var install = _temp.FilePath("game");
        Directory.CreateDirectory(Path.Combine(install, ".yagl", "bridge"));
        File.WriteAllText(Path.Combine(install, "PlatformProcess.exe"), "fresh-candidate");
        File.WriteAllText(Path.Combine(install, ".yagl", "bridge", "Games.exe"), "stale-decoy");
        File.WriteAllText(Path.Combine(install, ".yagl", "bridge", "yagl-parent-bridge.exe"), "stale-helper");

        var prep = new DecoyParentBridge().TryPrepare(install);

        Assert.NotNull(prep);
        Assert.Equal("fresh-candidate", File.ReadAllText(prep.DecoyPath));
        Assert.Equal(DecoyParentBridge.ReadEmbeddedHelper(), File.ReadAllBytes(prep.HelperPath));
    }

    [Fact]
    public void TryPrepare_NoCandidate_ReturnsNullWithoutCreatingBridgeDirectory()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("桥仅 Linux 原生 umu 链使用");
        }

        var install = _temp.FilePath("game");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "EndfieldBase.dll"), "not-a-candidate");

        var prep = new DecoyParentBridge().TryPrepare(install);

        Assert.Null(prep);
        Assert.False(Directory.Exists(Path.Combine(install, ".yagl")));
    }

    [Fact]
    public void TryPrepare_MissingInstallDirectory_ReturnsNull()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("桥仅 Linux 原生 umu 链使用");
        }

        Assert.Null(new DecoyParentBridge().TryPrepare(_temp.FilePath("does-not-exist")));
    }

    [Fact]
    public void TryPrepare_BridgePathBlockedByFile_ReturnsNull()
    {
        //  ".yagl" 被同名的普通文件占住（或权限异常）：准备失败不抛，回退直启
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("桥仅 Linux 原生 umu 链使用");
        }

        var install = _temp.FilePath("game");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "PlatformProcess.exe"), "decoy");
        File.WriteAllText(Path.Combine(install, ".yagl"), "blocker");

        Assert.Null(new DecoyParentBridge().TryPrepare(install));
    }

    [Fact]
    public void BridgeSource_MatchesManagedBridgeContract()
    {
        // C↔C# 契约锚点（2026-10-18 复审加固）：helper 的环境变量名由 C# 常量生成、退出码与
        // bridge.c 头注释一致——两侧测试都引用同一常量，单方改名在其它单例里不可见
        // （失败形态是 helper 退出 2、游戏不出现而 UI 报「已启动」），故直接读 C 源码锚定。
        var source = File.ReadAllText(FindBridgeSourcePath());

        foreach (var variable in new[]
                 {
                     DecoyParentBridge.DecoyVariable,
                     DecoyParentBridge.TargetVariable,
                     DecoyParentBridge.CommandLineVariable,
                     DecoyParentBridge.WorkingDirectoryVariable,
                 })
        {
            Assert.Contains($"L\"{variable}\"", source, StringComparison.Ordinal);
        }

        // 退出码契约（bridge.c 头注释）：2 = 必填输入缺失；3 = 诱饵创建/属性设置失败；4 = 游戏创建失败
        var codes = Regex.Matches(source, @"bridge_fail\(.*?,\s*(\d)\);")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "2", "3", "4" }, codes);
    }

    [Fact]
    public void EmbeddedHelper_IsPinnedPeBinary()
    {
        // 嵌入 helper 的完整性锚点：意外损坏/忘记同步都会在此变红。
        // 更新流程：改 tools/wine-parent-bridge/bridge.c → ./build.sh → ./verify.sh → 更新本哈希。
        var bytes = DecoyParentBridge.ReadEmbeddedHelper();

        Assert.True(bytes.Length > 0, "嵌入式 helper 不应为空");
        Assert.Equal(0x5A4D, BitConverter.ToUInt16(bytes, 0)); // 'MZ'
        Assert.Equal(
            0x8664,
            BitConverter.ToUInt16(bytes, BitConverter.ToInt32(bytes, 0x3C) + 4)); // PE32+ x86-64
        Assert.Equal(
            EmbeddedHelperSha256,
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    /// <summary>定位仓库内 tools/wine-parent-bridge/bridge.c（从测试 bin 目录向上找；找不到 = 运行环境异常）。</summary>
    private static string FindBridgeSourcePath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tools", "wine-parent-bridge", "bridge.c");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("找不到 tools/wine-parent-bridge/bridge.c（测试需在仓库内运行）");
    }
}
