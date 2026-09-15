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
    ILogger? logger = null) : IPatchApplier
{
    private readonly HpatchzApplierOptions _options = options ?? new();

    public async Task ApplyAsync(string patchFilePath, string oldDir, string newDir, CancellationToken cancellationToken = default)
    {
        // 预检先行：缺失/不可执行时给出可操作错误，而不是等 CreateProcess 抛难懂的 Win32Exception
        // （Windows 上 "拒绝访问"、Linux 上 "cannot find the file" 都看不出真实原因）
        var tool = ResolvePatchTool();
        if (tool is null)
        {
            throw new UpdateException(
                $"Patch tool not found or not executable: {_options.HpatchzPath}. " +
                "Install HDiffPatch and make sure hpatchz is on PATH (with the executable bit on Linux), " +
                "or point HpatchzPath at the full binary path.");
        }

        Directory.CreateDirectory(newDir);

        var arguments = $"-f \"{oldDir}\" \"{patchFilePath}\" \"{newDir}\"";
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
