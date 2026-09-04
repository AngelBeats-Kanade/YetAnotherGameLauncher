namespace YetAnotherGameLauncher.Core.Tests.Infrastructure;

/// <summary>测试用临时目录，析构时递归删除。</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "yagl-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string FilePath(string fileName) => System.IO.Path.Combine(Path, fileName);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // 删除失败不影响测试结果
        }
    }
}
