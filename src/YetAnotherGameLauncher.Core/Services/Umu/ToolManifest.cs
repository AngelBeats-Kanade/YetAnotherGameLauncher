using YetAnotherGameLauncher.Core.Abstractions;

namespace YetAnotherGameLauncher.Core.Services.Umu;

/// <summary>
/// 从 Proton 目录读取 toolmanifest.vdf / compatibilitytool.vdf，解析启动所需元数据。
/// </summary>
public sealed record ToolManifest(
    string ToolPath,
    string CommandLine,
    string LayerName,
    string? RequiredToolAppId,
    string DisplayName)
{
    /// <summary>该工具是否为 Proton（需要 wine prefix 布局）。</summary>
    public bool IsProton => string.Equals(LayerName, "proton", StringComparison.Ordinal);

    /// <summary>toolmanifest 是否声明了容器 Runtime（require_tool_appid）。</summary>
    public bool HasRequiredRuntime => !string.IsNullOrWhiteSpace(RequiredToolAppId);

    /// <summary>解析结果的 Runtime；无 appid 时为 host。</summary>
    public SteamRuntimeInfo RequiredRuntime =>
        SteamRuntimeCatalog.FromAppId(RequiredToolAppId) ?? SteamRuntimeCatalog.Host;

    /// <summary>
    /// 从 Proton 目录加载清单。缺 toolmanifest.vdf 时抛 <see cref="UpdateException"/>。
    /// </summary>
    /// <param name="toolPath">Proton 根目录（含 toolmanifest.vdf）。</param>
    public static ToolManifest Load(string toolPath)
    {
        var manifestPath = Path.Combine(toolPath, "toolmanifest.vdf");
        if (!File.Exists(manifestPath))
        {
            throw new UpdateException(
                $"Proton 目录「{toolPath}」缺少 toolmanifest.vdf，无法识别兼容层。");
        }

        var root = VdfMiniParser.ParseFile(manifestPath);
        var manifest = VdfMiniParser.GetObject(root, "manifest") ?? root;
        var commandLine = VdfMiniParser.GetString(manifest, "commandline")
            ?? throw new UpdateException(
                $"toolmanifest.vdf（{manifestPath}）缺少 commandline 字段。");
        var layerName = VdfMiniParser.GetString(manifest, "compatmanager_layer_name") ?? "";
        var requiredAppId = VdfMiniParser.GetString(manifest, "require_tool_appid");
        var displayName = toolPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        displayName = Path.GetFileName(displayName);

        var compatPath = Path.Combine(toolPath, "compatibilitytool.vdf");
        if (File.Exists(compatPath))
        {
            var compatRoot = VdfMiniParser.ParseFile(compatPath);
            var tools = VdfMiniParser.GetObject(compatRoot, "compatibilitytools", "compat_tools");
            if (tools is not null)
            {
                foreach (var value in tools.Values)
                {
                    if (value is Dictionary<string, object> tool &&
                        VdfMiniParser.GetString(tool, "display_name") is { Length: > 0 } name)
                    {
                        displayName = name;
                        break;
                    }
                }
            }
        }

        return new ToolManifest(toolPath, commandLine, layerName, requiredAppId, displayName);
    }

    /// <summary>
    /// 组装工具入口命令：把 commandline 里的 %verb% 换成实际 verb，
    /// container-runtime 且存在 umu 时优先 umu（与上游一致）。
    /// 返回 argv 列表：首元素为工具绝对路径（可能含空格，不在此处拆开），其余为参数。
    /// </summary>
    public IReadOnlyList<string> BuildEntryCommand(string verb)
    {
        var toolPath = Path.GetFullPath(ToolPath);
        var commandLine = CommandLine;
        if (string.Equals(LayerName, "container-runtime", StringComparison.Ordinal) &&
            File.Exists(Path.Combine(ToolPath, "umu")))
        {
            commandLine = commandLine.Replace("_v2-entry-point", "umu", StringComparison.Ordinal);
        }

        commandLine = commandLine.Replace("%verb%", verb, StringComparison.Ordinal).Trim();
        if (!commandLine.StartsWith('/'))
        {
            commandLine = "/" + commandLine.TrimStart('/');
        }

        // commandline 形如 "/proton %verb%"：首段是相对工具根的入口，其余是参数
        var parts = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return [Path.Combine(toolPath, "proton")];
        }

        var entry = Path.GetFullPath(Path.Combine(toolPath, parts[0].TrimStart('/')));
        return [entry, .. parts.Skip(1)];
    }
}
