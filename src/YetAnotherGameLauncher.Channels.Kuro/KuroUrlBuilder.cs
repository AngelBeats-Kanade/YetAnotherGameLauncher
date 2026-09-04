using YetAnotherGameLauncher.Channels.Kuro.Models;

namespace YetAnotherGameLauncher.Channels.Kuro;

/// <summary>库洛 CDN 节点选择：在 K1==1 且 K2==1 的节点中取 P 最大者。</summary>
public static class KuroCdnSelector
{
    public static string? SelectCdn(IReadOnlyList<KuroCdnNode> cdnList)
    {
        KuroCdnNode? best = null;
        foreach (var node in cdnList)
        {
            if (node.K1 != 1 || node.K2 != 1)
            {
                continue;
            }

            if (best is null || node.Priority > best.Priority)
            {
                best = node;
            }
        }

        return best?.Url;
    }
}

/// <summary>按库洛规则拼接资源与差分包下载地址。</summary>
public static class KuroUrlBuilder
{
    /// <summary>拼接资源文件地址：cdn + (fromFolder ?? resourcesBasePath) + dest，逐段转义。</summary>
    public static string BuildFileUrl(string cdnBase, string? folder, string dest) =>
        Build(cdnBase, folder, dest);

    /// <summary>
    /// 拼接差分包地址，按参考实现依次回退：
    /// patchEntry.baseUrl → default config.baseUrl → cdn + "resources/"。
    /// </summary>
    public static string BuildPatchUrl(
        string cdnBase, string? patchBaseUrl, string? defaultBaseUrl, string patchFile) =>
        Build(cdnBase, patchBaseUrl ?? defaultBaseUrl ?? "resources/", patchFile);

    private static string Build(string cdnBase, string? folder, string path)
    {
        var segments = $"{(folder?.Trim('/') ?? "")}/{path.Replace('\\', '/').TrimStart('/')}"
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return $"{cdnBase.TrimEnd('/')}/{string.Join('/', segments.Select(Uri.EscapeDataString))}";
    }
}
