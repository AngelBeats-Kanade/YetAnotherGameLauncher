using System.Text;

namespace YetAnotherGameLauncher.Core.Utilities;

/// <summary>启动选项行（含 %command% 占位符）严格解析失败的错误类别，供 UI 路由报错文案。</summary>
public enum LaunchLineErrorKind
{
    /// <summary>无错误。</summary>
    None,

    /// <summary>%command% 之前的环境变量区坏条目（缺 =/空键名/未闭合引号）。</summary>
    EnvironmentItem,

    /// <summary>%command% 之前出现不含 = 的裸 token——多为「想给游戏传参」的误解，
    /// 文案需指向 %command% 逃生门（与缺 =/空键名的 KEY=VALUE 提示区分）。</summary>
    EnvironmentBareToken,

    /// <summary>%command% 之后的参数区存在未闭合引号。</summary>
    ArgumentsQuote,

    /// <summary>%command% 占位符出现多次（只认第一个）。</summary>
    DuplicateCommandPlaceholder,
}

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
    /// <summary>Steam 启动选项占位符：标记游戏命令在启动选项行中的位置（小写字面量）。</summary>
    public const string CommandPlaceholder = "%command%";

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
            if (value.AsSpan().IndexOfAny([' ', '\t', '\r', '\n', '"', '\'', '\\']) >= 0)
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

    /// <summary>严格解析（保存校验用，含 %command% 切分）：占位符之前的条目须为 KEY=VALUE（进
    /// <paramref name="environment"/>），之后的 token 作为游戏命令行参数（进 <paramref name="arguments"/>，
    /// 已去引号）。失败时 <paramref name="errorKind"/> 给出错误区类别，<paramref name="badItem"/> 返回
    /// 坏条目原文；坏条目之前的条目已进字典（保存侧整体弃用，语义与迁移前的纯环境严格入口一致）。
    /// 错误判定顺序：环境区坏条目（裸 token / 缺 =）→ 未闭合引号（按区归位）→ 重复占位符。</summary>
    public static bool TryParseLaunchLine(
        string text,
        out Dictionary<string, string> environment,
        out IReadOnlyList<string> arguments,
        out string badItem,
        out LaunchLineErrorKind errorKind)
    {
        environment = [];
        arguments = [];
        badItem = "";
        errorKind = LaunchLineErrorKind.None;

        var items = new List<(string Item, int RawStart, int RawEnd)>();
        var complete = TryTokenize(text, items, out var unterminatedRawStart);
        var placeholderIndex = items.FindIndex(item => item.Item == CommandPlaceholder);
        // 占位符在原文的起点（无占位符时取最大值，未闭合引号残段必然落在环境区）
        var placeholderRawStart = placeholderIndex >= 0 ? items[placeholderIndex].RawStart : int.MaxValue;

        // 环境区先解析（未闭合引号时残段不在 items 里，完整条目照常进字典——旧契约）
        var envCount = placeholderIndex >= 0 ? placeholderIndex : items.Count;
        for (var i = 0; i < envCount; i++)
        {
            var (item, rawStart, rawEnd) = items[i];
            var sep = item.IndexOf('=');
            if (sep <= 0)
            {
                // 裸 token（无 =）单独分类：多为「想给游戏传参」的误解，文案需指向 %command% 逃生门；
                // 空键名（= 左侧缺失）沿用「应为 KEY=VALUE」提示
                errorKind = sep < 0
                    ? LaunchLineErrorKind.EnvironmentBareToken
                    : LaunchLineErrorKind.EnvironmentItem;
                badItem = text[rawStart..rawEnd].Trim();
                return false; // 坏条目即停：之后的条目不再解析
            }

            environment[item[..sep]] = item[(sep + 1)..];
        }

        if (!complete)
        {
            errorKind = unterminatedRawStart >= placeholderRawStart
                ? LaunchLineErrorKind.ArgumentsQuote
                : LaunchLineErrorKind.EnvironmentItem;
            badItem = text[unterminatedRawStart..].Trim();
            return false;
        }

        if (items.Count(item => item.Item == CommandPlaceholder) > 1)
        {
            // 第二个占位符是多余结构：按原文报位（静默当参数会产出字面 %command% argv，反直觉）
            var second = items.First(item =>
                item.Item == CommandPlaceholder && item.RawStart > placeholderRawStart);
            errorKind = LaunchLineErrorKind.DuplicateCommandPlaceholder;
            badItem = text[second.RawStart..second.RawEnd].Trim();
            return false;
        }

        arguments = placeholderIndex >= 0
            ? items.Skip(placeholderIndex + 1).Select(item => item.Item).ToArray()
            : [];
        return true;
    }

    /// <summary>宽松解析（草稿合并/重写文本用，含 %command% 切分）：环境区坏条目（含未闭合引号
    /// 残段）静默跳过，占位符之后的 token 原样作为参数返回（多余的占位符按字面参数保留，
    /// 严格版保存时拒绝）。返回参数列表。</summary>
    public static IReadOnlyList<string> ParseLenientLaunchLine(
        string text, out Dictionary<string, string> environment)
    {
        environment = new Dictionary<string, string>(StringComparer.Ordinal);
        var items = new List<(string Item, int RawStart, int RawEnd)>();
        // 未闭合引号时 items 仍承载残段之前切出的完整条目，照常采纳
        TryTokenize(text, items, out _);
        var placeholderIndex = items.FindIndex(item => item.Item == CommandPlaceholder);
        var envCount = placeholderIndex >= 0 ? placeholderIndex : items.Count;
        for (var i = 0; i < envCount; i++)
        {
            var item = items[i].Item;
            var sep = item.IndexOf('=');
            if (sep <= 0)
            {
                continue;
            }

            environment[item[..sep]] = item[(sep + 1)..];
        }

        return placeholderIndex >= 0
            ? items.Skip(placeholderIndex + 1).Select(item => item.Item).ToArray()
            : [];
    }

    /// <summary>参数 token 列表 → 启动选项文本（空格拼接）：含空白/引号/反斜杠或空串的 token
    /// 双引号包裹并转义，回显可无损重解析。加引号判据与 <see cref="Serialize"/> 同一谓词
    ///（RF-14 教训：单引号也是词法开启符）；空串必须显式 ""，否则重解析时 token 消失。</summary>
    public static string SerializeArguments(IReadOnlyList<string> arguments)
    {
        var parts = new List<string>(arguments.Count);
        foreach (var argument in arguments)
        {
            if (argument.Length == 0
                || argument.AsSpan().IndexOfAny([' ', '\t', '\r', '\n', '"', '\'', '\\']) >= 0)
            {
                parts.Add($"\"{argument.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"");
            }
            else
            {
                parts.Add(argument);
            }
        }

        return string.Join(' ', parts);
    }
}
