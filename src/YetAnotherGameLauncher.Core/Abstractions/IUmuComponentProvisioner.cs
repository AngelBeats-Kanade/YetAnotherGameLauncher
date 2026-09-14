namespace YetAnotherGameLauncher.Core.Abstractions;

/// <summary>原生 umu 兼容组件（Proton / Steam Runtime）准备器。</summary>
public interface IUmuComponentProvisioner
{
    /// <summary>Proton 目录是否可用（存在 toolmanifest.vdf 与 proton 入口）。</summary>
    bool IsProtonReady(string protonPath);

    /// <summary>Steam Runtime 是否已安装且带完成标记。</summary>
    bool IsRuntimeReady(string runtimeVariant);

    /// <summary>
    /// 解析 Proton 请求（与 EnsureProtonAsync 相同的本地规则，不做下载）并读取其 toolmanifest，
    /// 返回该 Proton 实际要求的 Steam Runtime（Variant 为目录名、Name 为逻辑代号）。
    /// Proton 本地不存在、清单不可读或不需要容器 runtime 时返回 null，调用方自行回退默认 Runtime。
    /// </summary>
    /// <param name="protonRequest">绝对路径、版本名（GE-Proton9-27）或代号（GE-Proton / UMU-Proton）。</param>
    (string Variant, string Name)? ResolveRequiredRuntime(string protonRequest);

    /// <summary>
    /// 确保指定版本/代号的 Proton 就绪；缺失时从 GitHub 下载最新构建。
    /// 返回 Proton 绝对目录。
    /// </summary>
    /// <param name="protonRequest">绝对路径、版本名（GE-Proton9-27）或代号（GE-Proton / UMU-Proton）。</param>
    /// <param name="progress">下载/安装进度文本回调（供 UI 展示）；不需要时传 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<string> EnsureProtonAsync(
        string protonRequest,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>确保 Steam Runtime 就绪；缺失时从 repo.steampowered.com 下载并校验。</summary>
    /// <param name="runtimeVariant">Runtime 目录变体名。</param>
    /// <param name="runtimeName">Runtime 逻辑代号（显示用名称）。</param>
    /// <param name="progress">下载/安装进度文本回调（供 UI 展示）；不需要时传 null。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task EnsureRuntimeAsync(
        string runtimeVariant,
        string runtimeName,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}
