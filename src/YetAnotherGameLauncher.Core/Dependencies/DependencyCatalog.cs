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

            if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var url)
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

            if (manifest.Md5.Length != 32
                || !manifest.Md5.All(c => char.IsAsciiHexDigit(c)))
            {
                errors.Add($"{prefix}({manifest.Id}): md5 必须是 32 位十六进制。");
            }

            if (manifest.SizeBytes <= 0)
            {
                errors.Add($"{prefix}({manifest.Id}): sizeBytes 必须大于 0。");
            }

            if (string.IsNullOrWhiteSpace(manifest.ArchiveEntry)
                || manifest.ArchiveEntry.Contains('/')
                || manifest.ArchiveEntry.Contains('\\')
                || manifest.ArchiveEntry.Contains("..", StringComparison.Ordinal))
            {
                errors.Add($"{prefix}({manifest.Id}): archiveEntry 必须是压缩包内的纯文件名。");
            }

            if (manifest.Fonts.Count == 0)
            {
                errors.Add($"{prefix}({manifest.Id}): fonts 不能为空。");
            }

            foreach (var font in manifest.Fonts)
            {
                if (string.IsNullOrWhiteSpace(font.File)
                    || font.File.Contains('/')
                    || font.File.Contains('\\'))
                {
                    errors.Add($"{prefix}({manifest.Id}): fonts[].file 必须是纯文件名。");
                }

                if (font.Families.Count == 0 || font.Families.Any(string.IsNullOrWhiteSpace))
                {
                    errors.Add($"{prefix}({manifest.Id}): fonts[].families 不能为空且不得含空白项。");
                }
            }

            foreach (var group in manifest.ReplacementGroups)
            {
                if (string.IsNullOrWhiteSpace(group.Target))
                {
                    errors.Add($"{prefix}({manifest.Id}): replacementGroups[].target 不能为空。");
                }

                if (group.Replaces.Count == 0 || group.Replaces.Any(string.IsNullOrWhiteSpace))
                {
                    errors.Add($"{prefix}({manifest.Id}): replacementGroups[].replaces 不能为空且不得含空白项。");
                }
            }
        }

        return errors.Count > 0 ? throw new DependencyCatalogException(errors) : documents;
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
