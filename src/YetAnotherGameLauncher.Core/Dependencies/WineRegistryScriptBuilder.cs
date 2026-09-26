using System.Text;

namespace YetAnotherGameLauncher.Core.Dependencies;

/// <summary>
/// 依赖清单 → wine reg import 可导入的 .reg 脚本（Windows Registry Editor Version 5.00）。
/// 纯文本生成；UTF-16LE BOM 由写盘方补（wine reg import 对 V5 格式要求 UTF-16）。
/// 单脚本承载全部登记与替换：一次 <c>wine reg import</c> 替代逐键 <c>reg add</c>
/// （每键一次 wine 调用要拉起 30+ 次 wineserver 会话）。
/// </summary>
public static class WineRegistryScriptBuilder
{
    /// <summary>字体正式登记键（Windows 约定位置；Wine 启动时枚举此键补全名字映射）。</summary>
    private const string FontsKey = @"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows NT\CurrentVersion\Fonts";

    /// <summary>Wine 专属字体名替换键：程序请求旧字体名时 Wine 直接换发目标字体。</summary>
    private const string ReplacementsKey = @"HKEY_CURRENT_USER\Software\Wine\Fonts\Replacements";

    /// <summary>生成完整 .reg 内容（LF 换行；wine reg import 对换行风格不敏感，与 Bottles 同款）。</summary>
    public static string Build(DependencyManifest manifest)
    {
        var builder = new StringBuilder();
        builder.Append("Windows Registry Editor Version 5.00\n");

        if (manifest.Fonts.Count > 0)
        {
            builder.Append("\n[").Append(FontsKey).Append("]\n");
            foreach (var font in manifest.Fonts)
            {
                foreach (var family in font.Families)
                {
                    AppendValue(builder, family, font.File);
                }
            }
        }

        if (manifest.ReplacementGroups.Count > 0)
        {
            builder.Append("\n[").Append(ReplacementsKey).Append("]\n");
            foreach (var group in manifest.ReplacementGroups)
            {
                foreach (var replaced in group.Replaces)
                {
                    AppendValue(builder, replaced, group.Target);
                }
            }
        }

        return builder.ToString();
    }

    /// <summary>追加一行注册表值：.reg 文本格式要求 "名称"="数据"（两侧双引号）。</summary>
    private static void AppendValue(StringBuilder builder, string name, string data)
    {
        builder.Append('"').Append(Escape(name)).Append('"')
            .Append('=')
            .Append('"').Append(Escape(data)).Append('"')
            .Append('\n');
    }

    /// <summary>把内容按 wine reg import 要求写盘：UTF-16LE 带 BOM（V5 格式签名）。</summary>
    public static async Task WriteAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        // UTF-16LE + BOM：Encoding.Unicode 的 GetPreamble 即 FF FE， StreamWriter 落盘时自动写入
        await File.WriteAllTextAsync(path, content, Encoding.Unicode, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>.reg 值转义：反斜杠与双引号（.reg 文本格式仅这两个字符需要转义）。</summary>
    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
