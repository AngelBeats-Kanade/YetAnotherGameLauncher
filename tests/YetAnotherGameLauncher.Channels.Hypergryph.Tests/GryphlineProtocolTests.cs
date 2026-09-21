using System.Text.Json;
using Xunit;
using YetAnotherGameLauncher.Channels.Hypergryph.Models;

namespace YetAnotherGameLauncher.Channels.Hypergryph.Tests;

/// <summary>
/// 协议工具直测（2026-09-22 测试审计补齐：internal 且此前仅经渠道 API 间接覆盖——
/// 国服/国际服切换依赖 options 空值回退与 apiBase 去尾斜杠语义）。
/// </summary>
public class GryphlineProtocolTests
{
    [Fact]
    public void OptionOrDefault_PresentValue_ReturnsTrimmed()
    {
        var options = new Dictionary<string, string> { ["apiBase"] = " https://gp.zenless.zone " };

        Assert.Equal("https://gp.zenless.zone",
            GryphlineProtocol.OptionOrDefault(options, "apiBase", "fallback"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void OptionOrDefault_MissingOrWhitespace_FallsBack(string? raw)
    {
        var options = raw is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["apiBase"] = raw };

        Assert.Equal("fallback",
            GryphlineProtocol.OptionOrDefault(options, "apiBase", "fallback"));
    }

    [Fact]
    public void ApiBaseOrNull_Present_StripsTrailingSlashes()
    {
        var options = new Dictionary<string, string> { ["apiBase"] = "https://example.com///" };

        Assert.Equal("https://example.com", GryphlineProtocol.ApiBaseOrNull(options));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ApiBaseOrNull_MissingOrWhitespace_ReturnsNull(string? raw)
    {
        var options = raw is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["apiBase"] = raw };

        Assert.Null(GryphlineProtocol.ApiBaseOrNull(options));
    }

    [Fact]
    public void JsonOptions_SnakeCasePayload_DeserializesIntoDtos()
    {
        // 松散反序列化是版本/背景两个接口共用的解析口径：字段缺失不炸、大小写不敏感
        var response = JsonSerializer.Deserialize<GameVersionResponse>(
            """
            {
              "version": "0.8.81",
              "pkg": { "packs": [ { "url": "https://x/package.zip", "md5": "d41d8cd98f00b204e9800998ecf8427e", "package_size": "1024" } ] }
            }
            """,
            GryphlineProtocol.JsonOptions);

        Assert.NotNull(response);
        Assert.Equal("0.8.81", response.Version);
        Assert.NotNull(response.Pkg);
        var pack = Assert.Single(response.Pkg.Packs);
        Assert.Equal("https://x/package.zip", pack.Url);
        Assert.Equal("1024", pack.PackageSize);
        Assert.Null(response.Patch); // 非预下载窗口期 patch 缺失 → null（宽松语义）
    }
}
