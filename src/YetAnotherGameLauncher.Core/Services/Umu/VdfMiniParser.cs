using System.Text;

namespace YetAnotherGameLauncher.Core.Services.Umu;

/// <summary>
/// Valve VDF（KeyValues）最小解析器：只覆盖 toolmanifest.vdf / compatibilitytool.vdf
/// 需要的嵌套对象与基本字符串，不支持条件块、转义以外的花活。
/// </summary>
public static class VdfMiniParser
{
    /// <summary>解析 VDF 文本为嵌套字典（子表也是 Dictionary&lt;string, object&gt;）。</summary>
    public static Dictionary<string, object> Parse(string text)
    {
        var index = 0;
        return ParseObject(Tokenize(text), ref index);
    }

    /// <summary>从文件读取并解析。</summary>
    public static Dictionary<string, object> ParseFile(string path) =>
        Parse(File.ReadAllText(path, Encoding.UTF8));

    /// <summary>按路径段取嵌套对象；失败返回 null。</summary>
    public static Dictionary<string, object>? GetObject(IReadOnlyDictionary<string, object> root, params string[] path)
    {
        IReadOnlyDictionary<string, object>? current = root;
        foreach (var key in path)
        {
            if (current is null || !current.TryGetValue(key, out var next))
            {
                return null;
            }

            current = next as Dictionary<string, object>;
        }

        return current as Dictionary<string, object>;
    }

    /// <summary>按路径段取字符串值；失败返回 null。</summary>
    public static string? GetString(IReadOnlyDictionary<string, object> root, params string[] path)
    {
        if (path.Length == 0)
        {
            return null;
        }

        var parentPath = path[..^1];
        var leaf = path[^1];
        var parent = parentPath.Length == 0 ? root : GetObject(root, parentPath);
        if (parent is null || !parent.TryGetValue(leaf, out var value))
        {
            return null;
        }

        return value as string;
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c is '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c is '{' or '}')
            {
                tokens.Add(c.ToString());
                i++;
                continue;
            }

            if (c == '"')
            {
                i++;
                var sb = new StringBuilder();
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        i++;
                        sb.Append(text[i]);
                        i++;
                        continue;
                    }

                    sb.Append(text[i]);
                    i++;
                }

                i++; // closing quote
                tokens.Add(sb.ToString());
                continue;
            }

            // 裸 token（少见）
            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not ('{' or '}' or '"'))
            {
                i++;
            }

            tokens.Add(text[start..i]);
        }

        return tokens;
    }

    private static Dictionary<string, object> ParseObject(List<string> tokens, ref int index)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        while (index < tokens.Count)
        {
            var token = tokens[index];
            if (token == "}")
            {
                index++;
                return result;
            }

            var key = token;
            index++;
            if (index >= tokens.Count)
            {
                return result;
            }

            var next = tokens[index];
            if (next == "{")
            {
                index++;
                result[key] = ParseObject(tokens, ref index);
            }
            else
            {
                result[key] = next;
                index++;
            }
        }

        return result;
    }
}
