using System.Text.Json;

namespace YetAnotherGameLauncher.Channels.Kuro;

/// <summary>库洛启动器当期背景配置（来自官方背景内容 JSON）。</summary>
/// <param name="BackgroundFile">背景视频（mp4）CDN 直链。</param>
/// <param name="FirstFrameImage">首帧占位图（webp/png）直链，可缺省。</param>
/// <param name="Slogan">横幅图直链，可缺省。</param>
/// <param name="FunctionSwitch">背景功能开关（1 开启 / 0 关闭）；null = 字段缺省，按开启处理。</param>
/// <param name="BackgroundFileType">背景类型（2 = 视频）；null = 字段缺省，按视频处理（历史投放均为视频）。</param>
public sealed record KuroSwitchConfig(
    string BackgroundFile,
    string? FirstFrameImage,
    string? Slogan,
    int? FunctionSwitch = null,
    int? BackgroundFileType = null)
{
    /// <summary>背景是否为视频（backgroundFileType=2 或缺省）。</summary>
    public bool IsVideo => BackgroundFileType is null or 2;

    /// <summary>从背景配置的对象元素解析；backgroundFile 缺失/空白（官方未投放背景）或功能关闭（functionSwitch=0）返回 null。</summary>
    public static KuroSwitchConfig? FromJson(JsonElement root)
    {
        var backgroundFile = GetStringOrNull(root, "backgroundFile");
        if (string.IsNullOrWhiteSpace(backgroundFile) || GetIntOrNull(root, "functionSwitch") == 0)
        {
            return null;
        }

        return new KuroSwitchConfig(
            backgroundFile!,
            GetStringOrNull(root, "firstFrameImage"),
            GetStringOrNull(root, "slogan"),
            GetIntOrNull(root, "functionSwitch"),
            GetIntOrNull(root, "backgroundFileType"));

        // 读取 JSON 对象的字符串字段；缺失或非字符串返回 null。
        static string? GetStringOrNull(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        // 读取 JSON 对象的整数字段；缺失或非数值返回 null。
        static int? GetIntOrNull(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value)
                && (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n)
                    || (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out n)))
                ? n
                : null;
    }
}
