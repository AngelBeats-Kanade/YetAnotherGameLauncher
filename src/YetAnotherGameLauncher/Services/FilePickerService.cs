using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace YetAnotherGameLauncher.Services;

/// <summary>图片文件选择器抽象（便于无头测试替换）。</summary>
public interface IFilePickerService
{
    /// <summary>弹出图片文件选择对话框，返回选中的本地路径；取消返回 null。</summary>
    Task<string?> PickImageFileAsync(string title);
}

/// <summary>基于主窗口 StorageProvider 的实现。</summary>
public sealed class StorageProviderFilePicker : IFilePickerService
{
    /// <summary>弹出图片文件选择对话框，返回选中的本地路径；取消或无主窗口返回 null。</summary>
    public async Task<string?> PickImageFileAsync(string title)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null)
        {
            return null;
        }

        var files = await desktop.MainWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Image")
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"],
                },
            ],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
