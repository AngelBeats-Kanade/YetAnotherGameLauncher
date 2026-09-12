namespace YetAnotherGameLauncher.Core.Abstractions;

/// <summary>原生 umu 兼容组件（Proton / Steam Runtime）准备器。</summary>
public interface IUmuComponentProvisioner
{
    /// <summary>Proton 目录是否可用（存在 toolmanifest.vdf 与 proton 入口）。</summary>
    bool IsProtonReady(string protonPath);

    /// <summary>Steam Runtime 是否已安装且带完成标记。</summary>
    bool IsRuntimeReady(string runtimeVariant);

    /// <summary>
    /// 确保指定版本/代号的 Proton 就绪；缺失时从 GitHub 下载最新构建。
    /// 返回 Proton 绝对目录。
    /// </summary>
    /// <param name="protonRequest">绝对路径、版本名（GE-Proton9-27）或代号（GE-Proton / UMU-Proton）。</param>
    Task<string> EnsureProtonAsync(
        string protonRequest,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>确保 Steam Runtime 就绪；缺失时从 repo.steampowered.com 下载并校验。</summary>
    Task EnsureRuntimeAsync(
        string runtimeVariant,
        string runtimeName,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}
