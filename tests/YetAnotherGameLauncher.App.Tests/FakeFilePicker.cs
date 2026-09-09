using YetAnotherGameLauncher.Services;

namespace YetAnotherGameLauncher.AppTests;

/// <summary>
/// 可编程的文件/目录选择器假实现：用预设返回值模拟"用户选中/取消"，
/// 并记录最近一次目录选择调用的参数供断言（无系统对话框，完全离线）。
/// 放在 App.Tests：IFilePickerService 定义在 App 工程内，TestSupport 不引用它。
/// </summary>
public sealed class FakeFilePicker : IFilePickerService
{
    /// <summary>PickImageFileAsync 的预设返回值（null = 用户取消）。</summary>
    public string? ImageResult { get; set; }

    /// <summary>PickFolderAsync 的预设返回值（null = 用户取消）。</summary>
    public string? FolderResult { get; set; }

    /// <summary>PickExecutableFileAsync 的预设返回值（null = 用户取消）。</summary>
    public string? ExecutableResult { get; set; }

    /// <summary>最近一次目录选择调用的（标题, 建议目录），供断言起始位置逻辑。</summary>
    public (string Title, string? SuggestedPath)? LastFolderCall { get; private set; }

    /// <summary>最近一次可执行文件选择调用的（标题, 建议目录），供断言起始位置逻辑。</summary>
    public (string Title, string? SuggestedDirectory)? LastExecutableCall { get; private set; }

    /// <summary>返回预设的图片路径。</summary>
    public Task<string?> PickImageFileAsync(string title) => Task.FromResult(ImageResult);

    /// <summary>记录调用参数并返回预设的目录路径。</summary>
    public Task<string?> PickFolderAsync(string title, string? suggestedPath)
    {
        LastFolderCall = (title, suggestedPath);
        return Task.FromResult(FolderResult);
    }

    /// <summary>记录调用参数并返回预设的可执行文件路径。</summary>
    public Task<string?> PickExecutableFileAsync(string title, string? suggestedDirectory)
    {
        LastExecutableCall = (title, suggestedDirectory);
        return Task.FromResult(ExecutableResult);
    }
}
