using System.Security.Cryptography;

namespace YetAnotherGameLauncher.Core.Utilities;

/// <summary>MD5/SHA256 摘要工具（与各游戏渠道的清单校验格式一致：小写 hex）。</summary>
public static class Hashing
{
    /// <summary>字节数组的 MD5（小写 hex）。</summary>
    public static string Md5Hex(byte[] data) =>
        Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();

    /// <summary>文件的 MD5（流式读取，小写 hex）。</summary>
    public static string Md5Hex(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(MD5.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>文件的 SHA256（流式读取，小写 hex）；不整读内存，适合大安装包。</summary>
    public static async Task<string> Sha256HexAsync(string filePath, CancellationToken cancellationToken = default)
    {
        // CA2007 误报：await using 声明的 DisposeAsync 续体由编译器生成，无法对其追加 ConfigureAwait。
#pragma warning disable CA2007
        await using var stream = File.OpenRead(filePath);
#pragma warning restore CA2007
        return Convert.ToHexStringLower(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }
}
