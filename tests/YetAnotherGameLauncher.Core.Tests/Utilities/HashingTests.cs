using System.Security.Cryptography;
using YetAnotherGameLauncher.Core.Utilities;
using Xunit;

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
}
