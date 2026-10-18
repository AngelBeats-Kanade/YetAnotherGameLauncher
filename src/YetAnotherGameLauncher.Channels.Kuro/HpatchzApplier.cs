using Microsoft.Extensions.Logging;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Core.Utilities;

namespace YetAnotherGameLauncher.Channels.Kuro;

/// <summary>
/// 基于 hpatchz 的补丁应用器（库洛 krpdiff 即 HDiffPatch 目录差分格式；终末地 v2 更新载荷的
/// 单文件补丁同源，见 <see cref="ApplyFileAsync"/>）。
/// 命令形如：hpatchz -f &lt;oldPath&gt; &lt;patchFile&gt; &lt;newPath&gt;（目录与文件两种模式参数形状一致）。
/// 供给形态（HpatchzProvisioner 的社区验证构建）在 Linux 上经 wine 运行 hpatchz.exe
///（独立 WINEPREFIX、Z: 盘路径映射）；用户自备二进制原生直跑。
/// </summary>
public sealed class HpatchzApplier(
    IProcessRunner processRunner,
    HpatchzApplierOptions? options = null,
    ILogger? logger = null,
    IHpatchzProvisioner? provisioner = null) : IPatchApplier, IFilePatchApplier
{
    private readonly HpatchzApplierOptions _options = options ?? new();

    public async Task ApplyAsync(string patchFilePath, string oldDir, string newDir, CancellationToken cancellationToken = default)
    {
        // 目录模式需要产物目录存在（hpatchz 目录模式自己建深层目录的行为未验证，显式建更稳）
        Directory.CreateDirectory(newDir);
        await RunPatchAsync(oldDir, patchFilePath, newDir, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>单文件模式（终末地 v2 更新载荷的 .chk_patch）：hpatchz -f oldFile patchFile newFile。
    /// 产物目录由调用方保证存在——本方法不建目录（编排方的暂存目录是它的职责）。</summary>
    public Task ApplyFileAsync(
        string patchFilePath, string oldFilePath, string newFilePath, CancellationToken cancellationToken = default)
        => RunPatchAsync(oldFilePath, patchFilePath, newFilePath, cancellationToken);

    /// <summary>两种模式共用的执行链：工具解析（自备优先 → 供给兜底）→ 参数拼装（wine 映射）→ 执行 → 非零退出折算。</summary>
    private async Task RunPatchAsync(string oldPath, string patchFilePath, string newPath, CancellationToken cancellationToken)
    {
        // 预检先行：缺失/不可执行时给出可操作错误，而不是等 CreateProcess 抛难懂的 Win32Exception
        // （Windows 上 "拒绝访问"、Linux 上 "cannot find the file" 都看不出真实原因）。
        // 解析顺序（用户自备优先，2026-10-02 自动供给）：显式全路径只校验；裸名先 PATH；
        // 都没有且注入了供给器 → 自动下载社区验证构建兜底（鸣潮 krpdiff 与开源构建的兼容性
        // 见 HpatchzProvisioner 类注释——供给形态在 Linux 上是 wine + hpatchz.exe）
        var selfProvided = ResolvePatchTool();
        string fileName;
        string arguments;
        IReadOnlyDictionary<string, string>? environment = null;
        if (selfProvided is not null)
        {
            fileName = selfProvided;
            arguments = $"-f {QuoteArg(oldPath)} {QuoteArg(patchFilePath)} {QuoteArg(newPath)}";
        }
        else if (provisioner is not null)
        {
            HpatchzTool tool;
            try
            {
                tool = await provisioner.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 自动供给失败（下载/校验/平台不支持）与"补丁内容对不上"是两类问题：折算成
                // PatchToolUnavailable，增量链据此保留载荷暂存供修复后零重下重试（2026-10-18）
                throw new PatchToolUnavailableException(
                    $"Patch tool could not be provisioned: {ex.Message}", ex);
            }

            fileName = tool.FileName;
            environment = tool.Environment;
            // wine 形态：exe 路径作前缀参数，old/patch/new 转成 wine 的 Z: 盘路径；
            // Windows 供给的 exe 原生直跑（空前缀，路径原样）
            var prefix = tool.ArgumentPrefix.Length > 0 ? tool.ArgumentPrefix + " " : "";
            var isWine = tool.ArgumentPrefix.Length > 0;
            arguments = $"{prefix}-f {QuoteArg(MapPath(oldPath, isWine))} " +
                        $"{QuoteArg(MapPath(patchFilePath, isWine))} {QuoteArg(MapPath(newPath, isWine))}";
        }
        else
        {
            throw new PatchToolUnavailableException(
                $"Patch tool not found or not executable: {_options.HpatchzPath}. " +
                "Install HDiffPatch and make sure hpatchz is on PATH (with the executable bit on Linux), " +
                "or point HpatchzPath at the full binary path.");
        }

        logger?.LogDebug("Running {Exe} {Args}", fileName, arguments);

        ProcessResult result;
        try
        {
            result = await processRunner.RunAsync(
                new ProcessStartSpec(fileName, arguments, TimeoutMilliseconds: _options.TimeoutMilliseconds, Environment: environment),
                cancellationToken).ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // 进程起不来（TOCTOU 被删/坏 exe/wine 启动失败）同属工具/环境侧，不是补丁内容问题
            throw new PatchToolUnavailableException(
                $"Patch tool failed to start ({fileName}): {ex.Message}", ex);
        }

        if (!result.Succeeded)
        {
            throw new UpdateException(
                $"hpatchz exited with code {result.ExitCode} (patch: {Path.GetFileName(patchFilePath)}): {result.StandardError}");
        }
    }

    /// <summary>执行路径按形态映射：wine 形态转 Z: 盘路径，原生形态原样。</summary>
    private static string MapPath(string path, bool isWine) => isWine ? HpatchzProvisioner.ToWinePath(path) : path;

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
