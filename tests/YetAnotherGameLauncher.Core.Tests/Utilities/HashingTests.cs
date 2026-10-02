using System.Security.Cryptography;
using Xunit;
using YetAnotherGameLauncher.Core.Utilities;

namespace YetAnotherGameLauncher.Core.Tests.Utilities;

public class HashingTests
{
    [Fact]
    public void Md5Hex_Bytes_KnownVector()
    {
        // MD5("abc") = 900150983cd24fb0d6963f7d28e17f72
        var hex = Hashing.Md5Hex("abc"u8.ToArray());

        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", hex);
    }

    [Fact]
    public void Md5Hex_File_MatchesStreamHash()
    {
        var path = Path.Combine(Path.GetTempPath(), $"yagl-md5-{Guid.NewGuid():N}.tmp");
        try
        {
            var data = RandomNumberGenerator.GetBytes(4096);
            File.WriteAllBytes(path, data);
            var expected = Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();

            var actual = Hashing.Md5Hex(path);

            Assert.Equal(expected, actual);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Md5HexAsync_File_MatchesSyncVersion()
    {
        // 异步形态与同步版逐字节同值（2026-10-02 假死修复：组校验/暂存核验换流式异步哈希）
        var path = Path.Combine(Path.GetTempPath(), $"yagl-md5-{Guid.NewGuid():N}.tmp");
        try
        {
            var data = RandomNumberGenerator.GetBytes(8192);
            File.WriteAllBytes(path, data);

            var actual = await Hashing.Md5HexAsync(path);

            Assert.Equal(Hashing.Md5Hex(path), actual);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Md5HexAsync_Cancelled_ThrowsOperationCanceled()
    {
        var path = Path.Combine(Path.GetTempPath(), $"yagl-md5-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(1024));
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => Hashing.Md5HexAsync(path, cts.Token));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
