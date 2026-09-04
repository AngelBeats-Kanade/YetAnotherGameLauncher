using System.Security.Cryptography;

namespace YetAnotherGameLauncher.Core.Utilities;

/// <summary>MD5 摘要工具（与各游戏渠道的清单校验格式一致：小写 hex）。</summary>
public static class Hashing
{
    public static string Md5Hex(byte[] data) =>
        Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();

    public static string Md5Hex(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(MD5.HashData(stream)).ToLowerInvariant();
    }
}
