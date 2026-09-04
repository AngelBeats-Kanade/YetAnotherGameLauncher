using YetAnotherGameLauncher.Core.Abstractions;
using Microsoft.Extensions.Logging;

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
    private readonly IProcessRunner _processRunner = processRunner;
    private readonly HpatchzApplierOptions _options = options ?? new();

    public async Task ApplyAsync(string patchFilePath, string oldDir, string newDir, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(newDir);

        var arguments = $"-f \"{oldDir}\" \"{patchFilePath}\" \"{newDir}\"";
        logger?.LogDebug("执行 {Exe} {Args}", _options.HpatchzPath, arguments);

        var result = await _processRunner.RunAsync(
            new ProcessStartSpec(_options.HpatchzPath, arguments, TimeoutMilliseconds: _options.TimeoutMilliseconds),
            cancellationToken);

        if (!result.Succeeded)
        {
            throw new UpdateException(
                $"hpatchz 退出码 {result.ExitCode}（补丁：{Path.GetFileName(patchFilePath)}）：{result.StandardError}");
        }
    }
}
