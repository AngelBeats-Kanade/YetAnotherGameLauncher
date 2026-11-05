using System.Text.Json;
using YetAnotherGameLauncher.Core.Utilities;

namespace YetAnotherGameLauncher.Core.Dependencies;

/// <summary>依赖清单文档非法：携带全部校验错误（聚合式，修复者不必逐轮试探）。</summary>
public sealed class DependencyCatalogException(IReadOnlyList<string> errors)
    : Exception($"依赖清单校验失败：{string.Join("；", errors)}")
{
    /// <summary>全部校验错误（每条一句话，含清单定位信息）。</summary>
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// 依赖清单目录：解析内嵌 JSON 并做结构校验。清单是启动器自带数据（不可信输入的
/// 校验与 games.json 同级），解析失败一次性聚合全部错误。
/// </summary>
public static class DependencyCatalog
{
    /// <summary>Id 规则：小写字母/数字/连字符（完成标记文件名与本地化键由此派生，禁止歧义字符）。</summary>
    private static readonly System.Text.RegularExpressions.Regex IdPattern =
        new("^[a-z0-9-]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>winetricks verb 规则：作为 argv 一段直接传递，禁止空白/路径分隔符/前导连字符。</summary>
    private static readonly System.Text.RegularExpressions.Regex WinetricksVerbPattern =
        new("^[a-z0-9][a-z0-9._-]*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>DLL 覆盖键：可选 <c>*</c> 前缀 + 字母数字/._-（禁空格、路径分隔符、等号与控制字符）。</summary>
    private static readonly System.Text.RegularExpressions.Regex DllOverrideKeyPattern =
        new(@"^\*?[A-Za-z0-9._-]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>DLL 覆盖值：native/builtin/disabled 的逗号组合（wine DllOverrides 语义）。</summary>
    private static readonly System.Text.RegularExpressions.Regex DllOverrideValuePattern =
        new("^(native|builtin|disabled)(,(native|builtin|disabled))*$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>安装后校验用 SHA-256：64 位小写十六进制。</summary>
    private static readonly System.Text.RegularExpressions.Regex Sha256Pattern =
        new("^[0-9a-f]{64}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>解析依赖清单文档（camelCase、允许注释与尾逗号，与启动器自有 JSON 约定一致）。</summary>
    /// <exception cref="DependencyCatalogException">结构或字段非法；错误聚合在 <see cref="DependencyCatalogException.Errors"/>。</exception>
    public static IReadOnlyList<DependencyManifest> Parse(string json)
    {
        List<string> errors = [];
        List<DependencyManifest>? documents;
        try
        {
            documents = JsonSerializer.Deserialize<List<DependencyManifest>>(json, Json.Default);
        }
        catch (JsonException ex)
        {
            throw new DependencyCatalogException([$"文档不是合法 JSON 数组：{ex.Message}"]);
        }

        if (documents is null)
        {
            throw new DependencyCatalogException(["文档不是依赖清单数组。"]);
        }

        HashSet<string> seenIds = new(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < documents.Count; index++)
        {
            var manifest = documents[index];
            var prefix = $"清单[{index}]";
            // 顶层数组的 null 元素（JSON [null]）：与嵌套层同款归一为聚合错误
            if (manifest is null)
            {
                errors.Add($"{prefix}: 清单条目不能为空。");
                continue;
            }

            // STJ 对缺省键不给非空强制（未标 required）：null 会一路穿到字段校验，先归一为聚合错误
            if (string.IsNullOrWhiteSpace(manifest.Id))
            {
                errors.Add($"{prefix}: id 不能为空。");
                continue;
            }

            if (!IdPattern.IsMatch(manifest.Id))
            {
                errors.Add($"{prefix}({manifest.Id}): id 只能是小写字母/数字/连字符。");
            }

            if (!seenIds.Add(manifest.Id))
            {
                errors.Add($"{prefix}({manifest.Id}): id 重复。");
            }

            if (string.IsNullOrWhiteSpace(manifest.Version))
            {
                errors.Add($"{prefix}({manifest.Id}): version 不能为空。");
            }

            // 两种来源互斥：winetricks 来源不提供下载四字段/字体；下载来源不提供 dlls/覆盖。
            var hasDownloadSource = manifest.DownloadUrl is not null
                                    || manifest.FileName is not null
                                    || manifest.Md5 is not null
                                    || manifest.SizeBytes is not null;
            var hasWinetricksSource = manifest.WinetricksVerb is not null;
            if (hasDownloadSource && hasWinetricksSource)
            {
                errors.Add($"{prefix}({manifest.Id}): 来源混用：下载字段与 winetricksVerb 只能二选一。");
            }

            if (hasWinetricksSource)
            {
                ValidateWinetricksSource(manifest, prefix, errors);
            }
            else
            {
                ValidateDownloadSource(manifest, prefix, errors);
            }
        }

        return errors.Count > 0 ? throw new DependencyCatalogException(errors) : documents;
    }

    /// <summary>字体（下载）来源校验：下载四字段、fonts、replacementGroups 必填，dlls/覆盖不得出现。</summary>
    private static void ValidateDownloadSource(
        DependencyManifest manifest, string prefix, List<string> errors)
    {
        if (manifest.DownloadUrl is null
            || !Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var url)
            || url.Scheme != Uri.UriSchemeHttps)
        {
            errors.Add($"{prefix}({manifest.Id}): downloadUrl 必须是 https 绝对地址。");
        }

        if (string.IsNullOrWhiteSpace(manifest.FileName)
            || manifest.FileName.Contains('/')
            || manifest.FileName.Contains('\\'))
        {
            errors.Add($"{prefix}({manifest.Id}): fileName 必须是纯文件名（不含路径分隔符）。");
        }

        if (manifest.Md5 is null
            || manifest.Md5.Length != 32
            || !manifest.Md5.All(c => char.IsAsciiHexDigit(c)))
        {
            errors.Add($"{prefix}({manifest.Id}): md5 必须是 32 位十六进制。");
        }

        if (manifest.SizeBytes is null || manifest.SizeBytes <= 0)
        {
            errors.Add($"{prefix}({manifest.Id}): sizeBytes 必须大于 0。");
        }

        if (manifest.Fonts is null || manifest.Fonts.Count == 0)
        {
            errors.Add($"{prefix}({manifest.Id}): fonts 不能为空。");
        }
        else
        {
            foreach (var font in manifest.Fonts)
            {
                if (font is null)
                {
                    errors.Add($"{prefix}({manifest.Id}): fonts[] 条目不能为空。");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(font.File)
                    || font.File.Contains('/')
                    || font.File.Contains('\\')
                    || font.File.Split('/', '\\').Contains("..", StringComparer.Ordinal))
                {
                    errors.Add($"{prefix}({manifest.Id}): fonts[].file 必须是压缩包内的纯文件名（禁路径段）。");
                }

                if (font.Families is null
                    || font.Families.Count == 0
                    || font.Families.Any(string.IsNullOrWhiteSpace))
                {
                    errors.Add($"{prefix}({manifest.Id}): fonts[].families 不能为空且不得含空白项。");
                }
            }
        }

        if (manifest.ReplacementGroups is null)
        {
            errors.Add($"{prefix}({manifest.Id}): replacementGroups 不能为空。");
        }
        else
        {
            foreach (var group in manifest.ReplacementGroups)
            {
                if (group is null)
                {
                    errors.Add($"{prefix}({manifest.Id}): replacementGroups[] 条目不能为空。");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(group.Target))
                {
                    errors.Add($"{prefix}({manifest.Id}): replacementGroups[].target 不能为空。");
                }

                if (group.Replaces is null
                    || group.Replaces.Count == 0
                    || group.Replaces.Any(string.IsNullOrWhiteSpace))
                {
                    errors.Add($"{prefix}({manifest.Id}): replacementGroups[].replaces 不能为空且不得含空白项。");
                }
            }
        }

        // 空集合也算"出现了字段"：字体来源不得携带 dlls/dllOverrides（无论是否为空）
        if (manifest.Dlls is not null)
        {
            errors.Add($"{prefix}({manifest.Id}): dlls 只允许与 winetricksVerb 同时出现。");
        }

        if (manifest.DllOverrides is not null)
        {
            errors.Add($"{prefix}({manifest.Id}): dllOverrides 只允许与 winetricksVerb 同时出现。");
        }
    }

    /// <summary>winetricks 来源校验：verb/dlls/覆盖必填且合法，下载字段与字体字段必须为空。</summary>
    private static void ValidateWinetricksSource(
        DependencyManifest manifest, string prefix, List<string> errors)
    {
        if (manifest.WinetricksVerb is null || !WinetricksVerbPattern.IsMatch(manifest.WinetricksVerb))
        {
            errors.Add(
                $"{prefix}({manifest.Id}): winetricksVerb 只能是小写字母/数字/._-，且以字母或数字开头。");
        }

        if (manifest.DownloadUrl is not null
            || manifest.FileName is not null
            || manifest.Md5 is not null
            || manifest.SizeBytes is not null)
        {
            errors.Add($"{prefix}({manifest.Id}): winetricks 来源不得提供 downloadUrl/fileName/md5/sizeBytes。");
        }

        if (manifest.Fonts is { Count: > 0 })
        {
            errors.Add($"{prefix}({manifest.Id}): winetricks 来源不得提供 fonts。");
        }

        if (manifest.ReplacementGroups is { Count: > 0 })
        {
            errors.Add($"{prefix}({manifest.Id}): winetricks 来源不得提供 replacementGroups。");
        }

        if (manifest.Dlls is null || manifest.Dlls.Count == 0)
        {
            errors.Add($"{prefix}({manifest.Id}): dlls 不能为空。");
        }
        else
        {
            HashSet<string> seenArchitectures = new(StringComparer.Ordinal);
            foreach (var dll in manifest.Dlls)
            {
                if (dll is null)
                {
                    errors.Add($"{prefix}({manifest.Id}): dlls[] 条目不能为空。");
                    continue;
                }

                if (dll.Arch is not ("x64" or "x86"))
                {
                    errors.Add($"{prefix}({manifest.Id}): dlls[].arch 只能是 x64 或 x86。");
                }
                else if (!seenArchitectures.Add(dll.Arch))
                {
                    errors.Add($"{prefix}({manifest.Id}): dlls[].arch 重复：{dll.Arch}。");
                }

                if (string.IsNullOrWhiteSpace(dll.FileName)
                    || dll.FileName is "." or ".."
                    || dll.FileName.Contains('/')
                    || dll.FileName.Contains('\\')
                    || dll.FileName.Split('/', '\\').Contains("..", StringComparer.Ordinal))
                {
                    errors.Add($"{prefix}({manifest.Id}): dlls[].fileName 必须是纯文件名（禁路径段）。");
                }

                if (dll.Sha256 is null || !Sha256Pattern.IsMatch(dll.Sha256))
                {
                    errors.Add($"{prefix}({manifest.Id}): dlls[].sha256 必须是 64 位小写十六进制。");
                }

                if (dll.SizeBytes <= 0)
                {
                    errors.Add($"{prefix}({manifest.Id}): dlls[].sizeBytes 必须大于 0。");
                }
            }
        }

        if (manifest.DllOverrides is null || manifest.DllOverrides.Count == 0)
        {
            errors.Add($"{prefix}({manifest.Id}): dllOverrides 不能为空。");
        }
        else
        {
            foreach (var (key, value) in manifest.DllOverrides)
            {
                if (string.IsNullOrWhiteSpace(key) || !DllOverrideKeyPattern.IsMatch(key))
                {
                    errors.Add(
                        $"{prefix}({manifest.Id}): dllOverrides key 非法（只允许 * 前缀 + 字母数字/._-，禁空格与路径分隔符）：{key}");
                }

                if (string.IsNullOrWhiteSpace(value) || !DllOverrideValuePattern.IsMatch(value))
                {
                    errors.Add(
                        $"{prefix}({manifest.Id}): dllOverrides value 非法（只允许 native/builtin/disabled 的逗号组合）：{key}");
                }
            }
        }
    }

    /// <summary>加载内嵌于 Core 程序集的内置清单（catalog.json）；资源缺失属程序集损坏，直接抛出。</summary>
    public static IReadOnlyList<DependencyManifest> LoadEmbedded()
    {
        var resourceName = typeof(DependencyCatalog).Assembly.GetName().Name
                           + ".Dependencies.catalog.json";
        using var stream = typeof(DependencyCatalog).Assembly.GetManifestResourceStream(resourceName)
                           ?? throw new DependencyCatalogException([$"内嵌依赖清单资源缺失：{resourceName}"]);
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
}
