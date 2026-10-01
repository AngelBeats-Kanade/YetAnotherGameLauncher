using Microsoft.Extensions.Logging;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Core.Utilities;

namespace YetAnotherGameLauncher.Channels.Kuro;

/// <summary>
/// 基于 HDiffPatch 官方 hpatchz 的补丁应用器（库洛 krpdiff 即 HDiffPatch 目录差分格式）。
/// 命令形如：hpatchz -f &lt;oldDir&gt; &lt;patchFile&gt; &lt;newDir&gt;
/// 在 Linux 上使用原生 hpatchz 二进制，无需 wine。
/// </summary>
public sealed class HpatchzApplier(
    IProcessRunner processRunner,
    HpatchzApplierOptions? options = null,
    ILogger? logger = null,
    IHpatchzProvisioner? provisioner = null) : IPatchApplier
{
    private readonly HpatchzApplierOptions _options = options ?? new();

    public async Task ApplyAsync(string patchFilePath, string oldDir, string newDir, CancellationToken cancellationToken = default)
    {
        // 预检先行：缺失/不可执行时给出可操作错误，而不是等 CreateProcess 抛难懂的 Win32Exception
        // （Windows 上 "拒绝访问"、Linux 上 "cannot find the file" 都看不出真实原因）。
        // 解析顺序（用户自备优先，2026-10-02 自动供给）：显式全路径只校验；裸名先 PATH；
        // 都没有且注入了供给器 → 自动下载官方固定版本兜底（对齐 FFmpeg/umu 供给哲学）
        var tool = ResolvePatchTool();
        if (tool is null && provisioner is not null)
        {
            tool = await provisioner.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        }

        if (tool is null)
        {
            throw new UpdateException(
                $"Patch tool not found or not executable: {_options.HpatchzPath}. " +
                "Install HDiffPatch and make sure hpatchz is on PATH (with the executable bit on Linux), " +
                "or point HpatchzPath at the full binary path.");
        }

        Directory.CreateDirectory(newDir);

        var arguments = $"-f {QuoteArg(oldDir)} {QuoteArg(patchFilePath)} {QuoteArg(newDir)}";
        logger?.LogDebug("Running {Exe} {Args}", tool, arguments);

        var result = await processRunner.RunAsync(
            new ProcessStartSpec(tool, arguments, TimeoutMilliseconds: _options.TimeoutMilliseconds),
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new UpdateException(
                $"hpatchz exited with code {result.ExitCode} (patch: {Path.GetFileName(patchFilePath)}): {result.StandardError}");
        }
    }

    /// <summary>按 .NET 命令行分词规则（CommandLineToArgvW 语义）包裹参数（F63）：
    /// 反斜杠仅在紧邻引号时才有转义语义——引号前的连续 `\` 翻倍后跟 `\"`、收尾闭合引号前的
    /// 连续 `\` 翻倍，**其余位置的反斜杠原样保留**。首版实现曾无条件翻倍全部 `\`
    /// （review P1 实锤：.NET 分词器对非引号前的 `\\` 原样保留，`C:\old` 被传成 `C:\\old`、
    /// 尾随 `\` 折叠成字面引号——Windows 全路径形态必错）；手工引号拼接则对含 `"` 路径
    /// 破坏引号配对。</summary>
    internal static string QuoteArg(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length + 8);
        sb.Append('"');
        var backslashes = 0;
        foreach (var ch in value)
        {
            if (ch == '\\')
            {
                backslashes++;
                continue;
            }

            if (ch == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                sb.Append('\\', backslashes).Append(ch);
            }

            backslashes = 0;
        }

        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// 预检补丁工具位置。全路径直接校验存在与可执行（Linux 含执行位要求，见
    /// FileUtilities.IsExecutableFile）；裸名按 PATH 逐目录解析，Windows 上对无扩展名再补试
    /// ".exe"——CreateProcess 会自动补全而 File.Exists 不会，不补这一刀会在 Windows 上误报缺失。
    /// 用户自备的二进制不做自动 chmod（与启动器自行下载的运行时不同，保持最小惊讶）。
    /// </summary>
    private string? ResolvePatchTool()
    {
        var command = _options.HpatchzPath;
        if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
        {
            return FileUtilities.IsExecutableFile(command) ? command : null;
        }

        return CompatTools.FindOnPath(command)
               ?? (OperatingSystem.IsWindows() && !Path.HasExtension(command)
                   ? CompatTools.FindOnPath(command + ".exe")
                   : null);
    }
}
