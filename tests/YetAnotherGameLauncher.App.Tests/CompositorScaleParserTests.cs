using System.Text.Json;
using Xunit;
using YetAnotherGameLauncher.Services;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// hyprctl monitors -j 输出解析（Phase 4c，2026-09-19，P4：Program 的 Xft.dpi 同步链路提取纯函数）：
/// 缩放值合并规则——合理区间过滤、多屏取最大、坏输入按调用方契约抛 JsonException。
/// </summary>
public class CompositorScaleParserTests
{
    [Fact]
    public void MultipleMonitors_TakesLargestScale()
    {
        const string json = """
            [
              { "name": "DP-1", "scale": 1.0 },
              { "name": "DP-2", "scale": 1.6666666 },
              { "name": "HDMI-A-1", "scale": 1.25 }
            ]
            """;

        Assert.Equal(1.6666666, CompositorScaleParser.ParseMaxMonitorScale(json));
    }

    [Fact]
    public void OutOfRangeOrNonNumberScales_Filtered()
    {
        // 0.4/6 超出 0.5-5 合理区间；scale 为字符串（异常输出）按无效丢弃
        const string json = """
            [
              { "name": "DP-1", "scale": 0.4 },
              { "name": "DP-2", "scale": "auto" },
              { "name": "DP-3", "scale": 6 },
              { "name": "DP-4", "scale": 2 }
            ]
            """;

        Assert.Equal(2, CompositorScaleParser.ParseMaxMonitorScale(json));
    }

    [Fact]
    public void BoundaryScales_AreInclusive()
    {
        Assert.Equal(0.5, CompositorScaleParser.ParseMaxMonitorScale("""[{ "scale": 0.5 }]"""));
        Assert.Equal(5, CompositorScaleParser.ParseMaxMonitorScale("""[{ "scale": 5 }]"""));
    }

    [Fact]
    public void NoUsableScale_ReturnsNull()
    {
        Assert.Null(CompositorScaleParser.ParseMaxMonitorScale("[]"));
        Assert.Null(CompositorScaleParser.ParseMaxMonitorScale("""[{ "name": "DP-1" }]""")); // 缺 scale 字段
        Assert.Null(CompositorScaleParser.ParseMaxMonitorScale("""[{ "scale": 99 }]""")); // 全部越界
    }

    [Fact]
    public void MalformedJson_ThrowsForCallerCatch()
    {
        // 契约：解析失败以 JsonException 上抛，Program 侧 catch 兜底返回 null（按原样启动）
        Assert.ThrowsAny<JsonException>(() => CompositorScaleParser.ParseMaxMonitorScale("{not-json"));
    }
}
