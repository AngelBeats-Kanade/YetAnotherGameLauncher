using System.Text;

namespace YetAnotherGameLauncher.Core.Utilities;

/// <summary>
/// 自定义启动选项文本（Steam 启动选项风格）与键值字典的互转。
/// 词法规则：条目以空白（空格/Tab/CR/LF）分隔；双引号内反斜杠仅转义反斜杠与双引号（不做变量展开，
/// 其余字符原样保留）；单引号内全字面；引号外反斜杠转义下一字符；引号可跨行；
/// 条目按第一个 = 切分键值，键名非空；同键名后值覆盖前值。
/// 2026-10-03 语义变更：旧「每行一条 KEY=VALUE、= 两侧容忍空格」格式 → shell 风格词法，
/// 旧格式文本（不含未加引号的空格值）解析结果不变。
/// </summary>
public static class LaunchOptionsText
{
    /// <summary>严格解析（保存校验用）：遇到坏条目（未闭合引号/缺 =/空键名）整体失败，
    /// <paramref name="badItem"/> 返回坏条目原文（未闭合引号时含残段），坏条目之前的条目已进字典。</summary>
    public static bool TryParse(string text, out Dictionary<string, string> environment, out string badItem)
    {
        environment = [];
        badItem = "";
        var items = new List<(string Item, int RawStart, int RawEnd)>();
        var complete = TryTokenize(text, items, out var unterminatedRawStart);
        foreach (var (item, rawStart, rawEnd) in items)
        {
            var sep = item.IndexOf('=');
            if (sep <= 0)
            {
                badItem = text[rawStart..rawEnd].Trim();
                return false; // 坏条目即停：之后的条目不再解析
            }

            environment[item[..sep]] = item[(sep + 1)..];
        }

        if (!complete)
        {
            badItem = text[unterminatedRawStart..].Trim();
            return false;
        }

        return true;
    }

    /// <summary>宽松解析（草稿合并/发行版检测用）：坏条目（含未闭合引号残段）静默跳过，不报错。</summary>
    public static Dictionary<string, string> ParseLenient(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var items = new List<(string Item, int RawStart, int RawEnd)>();
        // 未闭合引号时 items 仍承载残段之前切出的完整条目，照常采纳
        TryTokenize(text, items, out _);
        foreach (var (item, _, _) in items)
        {
            var sep = item.IndexOf('=');
            if (sep <= 0)
            {
                continue;
            }

            result[item[..sep]] = item[(sep + 1)..];
        }

        return result;
    }

    /// <summary>键值字典 → 启动选项文本（每行一条）：值含空白/引号/反斜杠时双引号包裹并转义，
    /// 回显可无损重解析。键名含空白或 = 时无法无损回显（正常输入不可达，仅直接改 JSON 可构造），
    /// 此类文本重解析失败由保存校验拦截。</summary>
    public static string Serialize(IReadOnlyDictionary<string, string> environment)
    {
        var lines = new List<string>(environment.Count);
        foreach (var (key, value) in environment)
        {
            if (value.AsSpan().IndexOfAny([' ', '\t', '\r', '\n', '"', '\\']) >= 0)
            {
                lines.Add($"{key}=\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"");
            }
            else
            {
                lines.Add($"{key}={value}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>词法切分：把文本切成去引号后的条目串，并记录每条在原文中的起止（坏条目报错定位用）。
    /// 返回 false 表示存在未闭合引号，<paramref name="unterminatedRawStart"/> 为残条目在原文的起点，
    /// 此时 <paramref name="items"/> 仍承载残条目之前切出的完整条目。</summary>
    private static bool TryTokenize(
        string text, List<(string Item, int RawStart, int RawEnd)> items, out int unterminatedRawStart)
    {
        unterminatedRawStart = -1;
        var item = new StringBuilder();
        var rawStart = -1;
        var quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote == '\0')
            {
                if (c is ' ' or '\t' or '\r' or '\n')
                {
                    if (rawStart >= 0)
                    {
                        items.Add((item.ToString(), rawStart, i));
                        item.Clear();
                        rawStart = -1;
                    }

                    continue;
                }

                if (rawStart < 0)
                {
                    rawStart = i;
                }

                if (c == '\\')
                {
                    i++;
                    if (i < text.Length)
                    {
                        item.Append(text[i]);
                    }
                    else
                    {
                        item.Append('\\'); // 文末孤立反斜杠按字面保留
                    }
                }
                else if (c is '"' or '\'')
                {
                    quote = c;
                }
                else
                {
                    item.Append(c);
                }
            }
            else if (quote == '"')
            {
                if (c == '\\' && i + 1 < text.Length && text[i + 1] is '"' or '\\')
                {
                    item.Append(text[i + 1]);
                    i++;
                }
                else if (c == '"')
                {
                    quote = '\0';
                }
                else
                {
                    item.Append(c);
                }
            }
            else
            {
                // 单引号：全字面直到闭引号
                if (c == '\'')
                {
                    quote = '\0';
                }
                else
                {
                    item.Append(c);
                }
            }
        }

        if (quote != '\0')
        {
            unterminatedRawStart = rawStart;
            return false;
        }

        if (rawStart >= 0)
        {
            items.Add((item.ToString(), rawStart, text.Length));
        }

        return true;
    }
}
