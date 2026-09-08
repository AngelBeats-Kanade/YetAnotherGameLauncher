using System.Text;
using System.Text.RegularExpressions;

namespace YetAnotherGameLauncher.Core.Services;

/// <summary>缓存文本匹配：命中的文本片段 + 匹配位置邻近窗口内发现的时间戳（无则 null）。</summary>
/// <param name="Text">匹配到的文本（通常是一个 JSON 配置对象）。</param>
/// <param name="NearbyTimestamp">邻近窗口内时间戳正则捕获的最大数值（用于多份历史响应间取最新）；未找到为 null。</param>
public sealed record CacheTextMatch(string Text, long? NearbyTimestamp);

/// <summary>
/// Chromium WebView（WebView2/CEF）磁盘缓存文本扫描器：官方启动器的运营配置（如背景视频地址）
/// 会随网页请求被持久化在 Cache_Data 的缓存块/独立文件里，本扫描器将其当作带噪声的文本流做正则提取。
/// 文件可能达数十 MB，按块流式扫描（块间留重叠窗口防正则跨界漏配）。
/// </summary>
public static partial class WebViewCacheScanner
{
    /// <summary>单个扫描块的长度（字节）。</summary>
    private const int ChunkSize = 8 * 1024 * 1024;

    /// <summary>相邻块的重叠长度（字节）：只需覆盖正则的最大匹配跨度与时间戳窗口。</summary>
    private const int ChunkOverlap = 4 * 1024;

    /// <summary>默认的时间戳邻近窗口（字节）：配置对象与请求 key（…switch.json?_t=…）通常相邻存放。</summary>
    private const int DefaultTimestampWindow = 2048;

    /// <summary>Latin1 编码：任意字节序列 1:1 无损映射为字符串，扫描完即弃，不做解码校验。</summary>
    private static readonly Latin1Encoding Latin1 = new();

    /// <summary>
    /// 在缓存目录的<b>所有文件</b>中扫描正则命中。返回去重后的命中列表（同一文本多次出现只保留
    /// 时间戳最大的一条）；找不到目录或文件不可读时返回空列表。
    /// </summary>
    /// <param name="cacheRoots">缓存目录根（递归枚举全部文件，如多个渠道/游戏的 Cache_Data 目录）。</param>
    /// <param name="pattern">要提取的文本模式（应匹配完整配置对象等自足片段）。</param>
    /// <param name="timestampPattern">可选时间戳模式（第一个捕获组必须是纯数字，如 switch.json?_t=1749488617）。</param>
    public static IReadOnlyList<CacheTextMatch> FindMatches(
        IEnumerable<string> cacheRoots, Regex pattern, Regex? timestampPattern = null)
    {
        var latest = new Dictionary<string, long?>(StringComparer.Ordinal);
        foreach (var root in cacheRoots)
        {
            foreach (var file in EnumerateFiles(root))
            {
                try
                {
                    foreach (var (text, timestamp) in ScanFile(file, pattern, timestampPattern))
                    {
                        // 同一配置在多份缓存响应里重复出现：按文本去重，保留时间戳最大的一条
                        if (!latest.TryGetValue(text, out var existing)
                            || (timestamp ?? long.MinValue) > (existing ?? long.MinValue))
                        {
                            latest[text] = timestamp;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 单个缓存文件损坏/被占用（官方启动器正在运行时常见）：跳过继续扫
                }
            }
        }

        return [.. latest.Select(kv => new CacheTextMatch(kv.Key, kv.Value))];
    }

    /// <summary>枚举缓存根下的全部文件；目录不存在时返回空。</summary>
    private static IEnumerable<string> EnumerateFiles(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        try
        {
            // 立即物化：惰性枚举在后续读取时才抛 IOException/UnauthorizedAccessException 会逃出 try
            return [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>按块流式扫描单个文件；返回（文本, 邻近时间戳）对。</summary>
    private static IEnumerable<(string Text, long? Timestamp)> ScanFile(
        string path, Regex pattern, Regex? timestampPattern)
    {
        using var stream = File.OpenRead(path);
        var buffer = new byte[ChunkSize];
        var previousTail = 0;
        int read;
        while ((read = FillBuffer(stream, buffer, previousTail)) > 0)
        {
            var text = Latin1.GetString(buffer, 0, read);
            // Match（class）而非 EnumerateMatches（ref struct 枚举器无法跨 yield 保留）
            for (var match = pattern.Match(text); match.Success; match = match.NextMatch())
            {
                yield return (match.Value, FindNearbyTimestamp(text, match.Index, match.Length, timestampPattern));
            }

            // 保留块尾重叠，防止正则命中/时间戳窗口跨块边界
            previousTail = Math.Min(read, ChunkOverlap);
            if (read < buffer.Length)
            {
                break;
            }

            Buffer.BlockCopy(buffer, read - previousTail, buffer, 0, previousTail);
        }
    }

    /// <summary>从流中填充缓冲区：先已有的尾部重叠 + 尽可能多的新数据；返回有效字节数。</summary>
    private static int FillBuffer(FileStream stream, byte[] buffer, int previousTail)
    {
        if (previousTail > 0 && stream.Position == stream.Length)
        {
            // 文件已读完但还有重叠尾巴：只扫重叠段（极端情况下正好命中跨块边界）
            return previousTail;
        }

        Buffer.BlockCopy(buffer, 0, buffer, 0, previousTail);
        var total = previousTail;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    /// <summary>在匹配位置邻近窗口内找时间戳模式的最大捕获值；窗口越大误配风险越高，默认 2KB。</summary>
    private static long? FindNearbyTimestamp(string text, int index, int length, Regex? timestampPattern)
    {
        if (timestampPattern is null)
        {
            return null;
        }

        var windowStart = Math.Max(0, index - DefaultTimestampWindow);
        var windowLength = Math.Min(text.Length, index + length + DefaultTimestampWindow) - windowStart;
        long? best = null;
        for (var match = timestampPattern.Match(text, windowStart, windowLength);
             match.Success;
             match = match.NextMatch())
        {
            var group = match.Groups.Count > 1 ? match.Groups[1] : match.Groups[0];
            if (long.TryParse(group.Value, out var value) && (best is null || value > best))
            {
                best = value;
            }
        }

        return best;
    }

    /// <summary>Latin1（ISO-8859-1）：单字节到字符的恒等映射，是扫描二进制流的唯一无损编码。</summary>
    private sealed class Latin1Encoding : Encoding
    {
        public override int GetByteCount(char[] chars, int index, int count) => count;

        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
        {
            for (var i = 0; i < charCount; i++)
            {
                bytes[byteIndex + i] = (byte)chars[charIndex + i];
            }

            return charCount;
        }

        public override string GetString(byte[] bytes, int index, int count)
        {
            var chars = new char[count];
            for (var i = 0; i < count; i++)
            {
                chars[i] = (char)bytes[index + i];
            }

            return new string(chars);
        }

        public override int GetCharCount(byte[] bytes, int index, int count) => count;

        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
        {
            for (var i = 0; i < byteCount; i++)
            {
                chars[charIndex + i] = (char)bytes[byteIndex + i];
            }

            return byteCount;
        }

        public override int GetMaxByteCount(int charCount) => charCount;

        public override int GetMaxCharCount(int byteCount) => byteCount;
    }
}
