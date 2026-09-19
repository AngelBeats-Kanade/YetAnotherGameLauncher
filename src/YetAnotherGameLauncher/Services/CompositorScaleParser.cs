using System.Text.Json;

namespace YetAnotherGameLauncher.Services;

/// <summary>
/// hyprctl monitors -j 输出的纯解析（Program 启动期 Xft.dpi 同步用）。
/// 抽成纯函数以便离线直测：真实链路需要 Hyprland 会话 + xrdb，测试无法构造。
/// </summary>
internal static class CompositorScaleParser
{
    /// <summary>
    /// 从显示器 JSON 数组解析合并缩放值：取 scale 字段在 0.5-5 合理区间的最大值
    /// （多显示器 UI 宁可大不可小）；空数组/无有效值返回 null（调用方不动 X 资源）。
    /// JSON 非法时抛 JsonException（由调用方兜底）。
    /// </summary>
    internal static double? ParseMaxMonitorScale(string json)
    {
        using var document = JsonDocument.Parse(json);
        var scales = document.RootElement.EnumerateArray()
            .Select(m => m.TryGetProperty("scale", out var scale)
                        && scale.ValueKind == JsonValueKind.Number
                ? scale.GetDouble()
                : double.NaN)
            .Where(s => s is >= 0.5 and <= 5)
            .ToList();
        return scales.Count > 0 ? scales.Max() : null;
    }
}
