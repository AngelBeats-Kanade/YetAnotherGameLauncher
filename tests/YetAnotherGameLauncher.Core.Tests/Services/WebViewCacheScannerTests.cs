using System.Text.RegularExpressions;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.TestSupport;
using Xunit;

namespace YetAnotherGameLauncher.Core.Tests.Services;

/// <summary>
/// Chromium 缓存文本扫描：正则提取 + 时间戳去重（同文本保留最新）+ 目录缺失/坏文件容错。
/// </summary>
public class WebViewCacheScannerTests : IDisposable
{
    private readonly TempDir _tempDir = new();

    public void Dispose() => _tempDir.Dispose();

    private static readonly Regex ConfigPattern = new(@"\{""backgroundFile"":""[^""]+""\}", RegexOptions.Compiled);
    private static readonly Regex TimestampPattern = new(@"switch\.json\?_t=(\d+)", RegexOptions.Compiled);

    private void WriteCacheFile(string relativePath, string content)
    {
        var path = _tempDir.FilePath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void FindMatches_ExtractsMatchesWithTimestamp()
    {
        WriteCacheFile("Cache_Data/data_1",
            """junk{"backgroundFile":"https://cdn/loop.mp4"}tail…switch.json?_t=1749488617…""");

        var matches = WebViewCacheScanner.FindMatches(
            [_tempDir.Path], ConfigPattern, TimestampPattern);

        var match = Assert.Single(matches);
        Assert.Contains("https://cdn/loop.mp4", match.Text, StringComparison.Ordinal);
        Assert.Equal(1749488617, match.NearbyTimestamp);
    }

    [Fact]
    public void FindMatches_DeduplicatesKeepingLatestTimestamp()
    {
        // 同一配置在多份缓存响应里重复：只保留时间戳最大的一条
        WriteCacheFile("Cache_Data/data_1",
            """{"backgroundFile":"https://cdn/old.mp4"}…switch.json?_t=100""");
        WriteCacheFile("Cache_Data/data_2",
            """{"backgroundFile":"https://cdn/old.mp4"}…switch.json?_t=200""");
        WriteCacheFile("Cache_Data/data_3",
            """{"backgroundFile":"https://cdn/new.mp4"}…switch.json?_t=300""");

        var matches = WebViewCacheScanner.FindMatches(
            [_tempDir.Path], ConfigPattern, TimestampPattern);

        Assert.Equal(2, matches.Count);
        Assert.Contains(matches, m => m.Text.Contains("old.mp4", StringComparison.Ordinal) && m.NearbyTimestamp == 200);
        Assert.Contains(matches, m => m.Text.Contains("new.mp4", StringComparison.Ordinal));
    }

    [Fact]
    public void FindMatches_MissingRootOrNoTimestamp_ReturnsGracefully()
    {
        // 目录不存在 → 空列表
        Assert.Empty(WebViewCacheScanner.FindMatches(
            [_tempDir.FilePath("nope")], ConfigPattern, TimestampPattern));

        // 有命中但没有时间戳 → 命中保留、时间戳为 null
        WriteCacheFile("Cache_Data/data_1", """{"backgroundFile":"https://cdn/loop.mp4"}""");
        var matches = WebViewCacheScanner.FindMatches([_tempDir.Path], ConfigPattern, TimestampPattern);

        var match = Assert.Single(matches);
        Assert.Null(match.NearbyTimestamp);
    }
}
