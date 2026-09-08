using System.Diagnostics.CodeAnalysis;

namespace YetAnotherGameLauncher.Core.Abstractions;

/// <summary>更新/安装流程中的业务失败（可向用户展示 message）。</summary>
public class UpdateException : Exception
{
    public UpdateException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>清单条目前置检查（增量文件/差分包/全量包等下载链路共用）。</summary>
public static class ManifestChecks
{
    /// <summary>断言清单条目携带下载地址，否则抛出结构化更新异常。</summary>
    /// <param name="url">清单给出的下载地址。</param>
    /// <param name="entryKind">条目种类（用于错误消息，如 "Package entry"）。</param>
    /// <param name="identity">条目标识（相对路径等）。</param>
    public static void EnsureDownloadUrl([NotNull] string? url, string entryKind, string identity)
    {
        if (url is null)
        {
            throw new UpdateException($"{entryKind} has no download URL: {identity}");
        }
    }
}
