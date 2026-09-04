namespace YetAnotherGameLauncher.Core.Abstractions;

/// <summary>更新/安装流程中的业务失败（可向用户展示 message）。</summary>
public class UpdateException : Exception
{
    public UpdateException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
