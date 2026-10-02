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

    /// <summary>文件的 MD5（异步流式读取，小写 hex）：大文件校验不长期占用调用线程且可取消
    ///（2026-10-02 假死修复：重入时全量已应用组 dstFiles 可达数十 GiB，同步 MD5 会冻结 UI 线程）。
    /// 逐块读取经 <paramref name="progress"/> 上报累计已读字节——与下载器回调同口径（IProgress&lt;long&gt;），
    /// 核验阶段借此复用下载进度/速度管道（2026-10-02 重跑预下载 0→100 跳变修复）。</summary>
    public static async Task<string> Md5HexAsync(
        string filePath, CancellationToken cancellationToken = default, IProgress<long>? progress = null)
    {
        // CA2007 误报：await using 声明的 DisposeAsync 续体由编译器生成，无法对其追加 ConfigureAwait。
#pragma warning disable CA2007
        await using var stream = File.OpenRead(filePath);
#pragma warning restore CA2007
        using var md5 = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.MD5);
        var buffer = new byte[64 * 1024];
        long totalRead = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            md5.AppendData(buffer, 0, read);
            totalRead += read;
            progress?.Report(totalRead);
        }

        return Convert.ToHexStringLower(md5.GetHashAndReset());
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
