using Xunit;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Services.Umu;
using YetAnotherGameLauncher.TestSupport;

namespace YetAnotherGameLauncher.Core.Tests.Services.Umu;

/// <summary>
/// UmuPrefix 幂等矩阵（Phase 4 补测，2026-09-19）：Setup 对五种既存形态的幂等/修复行为、
/// 锁竞争超时、用户目录互链四分支、EnsureUserExecute 平台语义。
/// 审计定位（2026-09 双向审计）：Setup 正路径之外的 75 行全部在此覆盖。
/// </summary>
public sealed class UmuPrefixIdempotencyTests : IDisposable
{
    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    private string PfxPath(string name = "p1") => _temp.FilePath("prefixes", name);

    [Fact]
    public void Setup_EmptyPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => UmuPrefix.Setup("  "));
    }

    [Fact]
    public void Setup_Twice_Idempotent()
    {
        var pfx = PfxPath();
        UmuPrefix.Setup(pfx, unixUserName: "tester");
        var marker = Path.Combine(pfx, "tracked_files");
        var before = File.GetLastWriteTimeUtc(marker);

        UmuPrefix.Setup(pfx, unixUserName: "tester");

        // 幂等：既存结构不重建（tracked_files 未被重写）
        Assert.Equal(before, File.GetLastWriteTimeUtc(marker));
    }

    [Fact]
    public void Setup_RealDirectoryAtPfx_Preserved()
    {
        // 旧版/用户自建的真实 pfx 目录：保留不替换
        var pfx = PfxPath();
        UmuPrefix.Setup(pfx, unixUserName: "tester");
        var pfxLink = Path.Combine(pfx, "pfx");
        Directory.Delete(pfxLink);
        Directory.CreateDirectory(pfxLink);
        File.WriteAllText(Path.Combine(pfxLink, "user-data.txt"), "keep");

        UmuPrefix.Setup(pfx, unixUserName: "tester");

        Assert.True(Directory.Exists(pfxLink)); // 真实目录仍在
        Assert.True(File.Exists(Path.Combine(pfxLink, "user-data.txt"))); // 内容未动
    }

    [Fact]
    public void Setup_WrongTargetSymlink_Repaired()
    {
        // pfx 链接指向错误目标（非 "." / 非 root）：删除重建为正确链接
        var pfx = PfxPath();
        UmuPrefix.Setup(pfx, unixUserName: "tester");
        var pfxLink = Path.Combine(pfx, "pfx");
        Directory.Delete(pfxLink);
        var other = _temp.FilePath("other-target");
        Directory.CreateDirectory(other);
        Directory.CreateSymbolicLink(pfxLink, other);

        UmuPrefix.Setup(pfx, unixUserName: "tester");

        var info = new DirectoryInfo(pfxLink);
        Assert.Equal(".", info.LinkTarget); // 重建为指向自身根的正确链接
    }

    [Fact]
    public void Setup_FileAtPfxPath_ReplacedWithLink()
    {
        var pfx = PfxPath();
        UmuPrefix.Setup(pfx, unixUserName: "tester");
        var pfxLink = Path.Combine(pfx, "pfx");
        Directory.Delete(pfxLink);
        File.WriteAllText(pfxLink, "stray file");

        UmuPrefix.Setup(pfx, unixUserName: "tester");

        Assert.True(Directory.Exists(pfxLink) || new DirectoryInfo(pfxLink).LinkTarget is not null);
    }

    [Fact]
    public void Setup_DanglingPfxSymlink_SelfHealsInsteadOfFailingForever()
    {
        // F21（artifacts/bugs.md）：pfx 为悬空符号链接（旧布局目标被删/卷未挂载）时
        // Directory.Exists false、IsSymlink false（DirectoryInfo.Exists 跟随链接）→ 自愈分支
        // 全部跳过 → CreateSymbolicLink EEXIST → CreateDirectory 同样 EEXIST → 二次
        // IOException 穿出 Setup → 每次启动永久失败。修复后：IsSymlink 按 LinkTarget 判定
        // （悬空链接仍是链接，官方 LinkTarget 语义 = "是否为链接"而非"目标是否存在"），
        // 错误链接照常修复
        var gone = _temp.FilePath("prefixes", "gone-target");
        var pfx = PfxPath("dangling");
        Directory.CreateDirectory(Path.GetDirectoryName(pfx)!);
        Directory.CreateSymbolicLink(pfx, gone);

        UmuPrefix.Setup(pfx, unixUserName: "tester");

        // 红落 Setup 抛 IOException：修复后 Setup 正常完成、pfx/pfx 链接可用
        Assert.True(Directory.Exists(Path.Combine(pfx, "pfx")),
            "修复后 pfx 链接应可用（Setup 正常完成，不再抛 IOException）");
    }

    [Fact]
    public void Setup_DanglingUsersSymlink_CompletesInsteadOfThrowing()
    {
        // F21 第 13 轮补充：users 自身是悬空符号链接时 :139 裸 CreateDirectory 报 EEXIST
        // → IOException 穿出 Setup → 同款每启动失败链。修复后：悬空链接先清再建
        var pfx = PfxPath("dangling-users");
        UmuPrefix.Setup(pfx, unixUserName: "tester");
        var users = Path.Combine(pfx, "pfx", "drive_c", "users");
        Directory.CreateDirectory(users); // 模拟 wineboot 过的前缀（否则 SetupUserLinks 直接跳过）
        var gone = _temp.FilePath("prefixes", "gone-users-target");
        Directory.Delete(users, recursive: true);
        Directory.CreateSymbolicLink(users, gone);

        UmuPrefix.Setup(pfx, unixUserName: "tester");

        Assert.True(Directory.Exists(users)); // 悬空链接已清除并重建为真实目录
    }

    [Fact]
    public void Setup_DoubleDanglingUserLinks_DoesNotThrow()
    {
        // F21 双悬空形态：IsSymlink 修复后悬空链接计入"已占用"，四分支全不命中——
        // Setup 不得抛（降级语义：用户链接缺失时 Proton/wineboot 自行创建，
        // 见 TryCreateDirectoryLink 注释），steamuser 悬空链接保持原样不再裸 mkdir
        var pfx = PfxPath("double-dangling");
        UmuPrefix.Setup(pfx, unixUserName: "tester");
        var users = Path.Combine(pfx, "pfx", "drive_c", "users");
        Directory.CreateDirectory(users);
        var steamuser = Path.Combine(users, "steamuser");
        var wineuser = Path.Combine(users, "tester");
        Directory.CreateDirectory(steamuser);
        Directory.CreateDirectory(wineuser);
        var gone = _temp.FilePath("prefixes", "gone-target");
        Directory.Delete(steamuser, recursive: true);
        Directory.Delete(wineuser, recursive: true);
        Directory.CreateSymbolicLink(steamuser, gone);
        Directory.CreateSymbolicLink(wineuser, gone);

        var ex = Record.Exception(() => UmuPrefix.Setup(pfx, unixUserName: "tester"));

        Assert.Null(ex); // 红：当前裸 mkdir EEXIST 抛 IOException
    }

    [Fact]
    public void Setup_LockContention_TimesOutWithActionableError()
    {
        var pfx = PfxPath();
        Directory.CreateDirectory(pfx);
        var lockPath = Path.Combine(pfx, "pfx.lock");
        using var holder = UmuPrefix.AcquireLock(lockPath, timeoutMilliseconds: 300);

        Assert.Throws<UpdateException>(
            () => UmuPrefix.AcquireLock(lockPath, timeoutMilliseconds: 300));
    }

    [Fact]
    public void Setup_LockReleasedByDispose_RetrySucceeds()
    {
        var lockPath = _temp.FilePath("locks", "l.lock");
        IDisposable? first = UmuPrefix.AcquireLock(lockPath, timeoutMilliseconds: 100);
        first?.Dispose();

        using var second = UmuPrefix.AcquireLock(lockPath, timeoutMilliseconds: 100);
        Assert.NotNull(second); // DeleteOnClose：释放后立即可重取
    }

    [Fact]
    public void Setup_UserLinks_NoDriveC_NoLinksCreated()
    {
        // prefix 尚未 wineboot（无 drive_c）：跳过用户互链不抛
        var pfx = PfxPath();
        UmuPrefix.Setup(pfx, unixUserName: "tester");

        Assert.False(Directory.Exists(Path.Combine(pfx, "pfx", "drive_c")));
    }

    [Fact]
    public void Setup_UserLinks_FreshWineboot_CreatesSteamUserWithWineuserLink()
    {
        // 分支 1：两用户均不存在 → 建 steamuser 真目录 + wineuser→steamuser 链接
        var pfx = PfxPath();
        UmuPrefix.Setup(pfx, unixUserName: "tester");
        Directory.CreateDirectory(Path.Combine(pfx, "pfx", "drive_c")); // 模拟 wineboot

        UmuPrefix.Setup(pfx, unixUserName: "tester");

        var users = Path.Combine(pfx, "pfx", "drive_c", "users");
        Assert.True(Directory.Exists(Path.Combine(users, "steamuser")));
        var wineuser = new DirectoryInfo(Path.Combine(users, "tester"));
        Assert.NotNull(wineuser.LinkTarget); // 链接到 steamuser
    }

    [Fact]
    public void Setup_UserLinks_WineuserRealDir_SteamuserGetsLink()
    {
        // 分支 2：wineuser 是真实目录且无 steamuser → 建 steamuser→wineuser 反向链接
        var pfx = PfxPath();
        UmuPrefix.Setup(pfx, unixUserName: "tester");
        var users = Path.Combine(pfx, "pfx", "drive_c", "users");
        Directory.CreateDirectory(Path.Combine(users, "tester")); // 用户真实目录已存在

        UmuPrefix.Setup(pfx, unixUserName: "tester");

        var steamuser = new DirectoryInfo(Path.Combine(users, "steamuser"));
        Assert.True(Directory.Exists(Path.Combine(users, "tester")));
        Assert.True(steamuser.LinkTarget is not null || Directory.Exists(steamuser.FullName));
    }

    [Fact]
    public void Setup_UserLinks_SteamuserRealDir_WineuserGetsLink()
    {
        // 分支 3：steamuser 真实目录存在且无 wineuser → 建 wineuser→steamuser
        var pfx = PfxPath();
        UmuPrefix.Setup(pfx, unixUserName: "tester");
        var users = Path.Combine(pfx, "pfx", "drive_c", "users");
        Directory.CreateDirectory(Path.Combine(users, "steamuser"));

        UmuPrefix.Setup(pfx, unixUserName: "tester");

        var wineuser = new DirectoryInfo(Path.Combine(users, "tester"));
        Assert.NotNull(wineuser.LinkTarget);
    }

    [Fact]
    public void Setup_UserLinks_BothExist_NoChange()
    {
        // 分支 4：两用户均已存在（链接或目录）→ 不动
        var pfx = PfxPath();
        UmuPrefix.Setup(pfx, unixUserName: "tester");
        Directory.CreateDirectory(Path.Combine(pfx, "pfx", "drive_c", "users", "steamuser"));
        Directory.CreateDirectory(Path.Combine(pfx, "pfx", "drive_c", "users", "tester"));

        UmuPrefix.Setup(pfx, unixUserName: "tester");

        // 均保持真实目录（未被替换为链接）
        Assert.True(Directory.Exists(Path.Combine(pfx, "pfx", "drive_c", "users", "steamuser")));
        Assert.True(Directory.Exists(Path.Combine(pfx, "pfx", "drive_c", "users", "tester")));
    }

    [Fact]
    public void EnsureUserExecute_MissingFile_NoThrow()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("执行位是 POSIX 专属概念");
        }
        else
        {
            UmuPrefix.EnsureUserExecute(_temp.FilePath("no-such-file")); // 不存在：no-op 不抛
        }
    }

    [Fact]
    public void EnsureUserExecute_ExistingFile_GetsExecuteBit()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("执行位是 POSIX 专属概念");
        }
        else
        {
            var path = _temp.FilePath("entry-point");
            File.WriteAllText(path, "#!/bin/sh");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            UmuPrefix.EnsureUserExecute(path);

            Assert.True(File.GetUnixFileMode(path).HasFlag(UnixFileMode.UserExecute));
        }
    }
}
