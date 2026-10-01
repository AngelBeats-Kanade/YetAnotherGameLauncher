using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;

namespace YetAnotherGameLauncher.TestSupport;

/// <summary>假下载器：从预设字典"下载"到目标路径，可模拟失败与内容损坏。</summary>
public sealed class FakeDownloader : IDownloader
{
    public Dictionary<string, byte[]> Responses { get; } = new(StringComparer.Ordinal);

    public List<string> Requests { get; } = [];

    public HashSet<string> FailUrls { get; } = new(StringComparer.Ordinal);

    /// <summary>分块上报进度（按 8 字节一块逐块 Report 累计值），模拟真实下载器的字节级回调；
    /// 默认单次 Report 全量长度（既有测试语义）。</summary>
    public bool ReportProgressInChunks { get; set; }

    public void Serve(string url, string content) => Responses[url] = System.Text.Encoding.UTF8.GetBytes(content);

    public void Serve(string url, byte[] content) => Responses[url] = content;

    public Task DownloadFileAsync(DownloadRequest request, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        Requests.Add(request.Url);
        if (FailUrls.Contains(request.Url))
        {
            throw new DownloadException($"假下载失败：{request.Url}");
        }

        var content = Responses[request.Url];
        Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
        File.WriteAllBytes(request.DestinationPath, content);
        if (ReportProgressInChunks && content.Length > 0)
        {
            var written = 0;
            while (written < content.Length)
            {
                written = Math.Min(written + 8, content.Length);
                progress?.Report(written);
            }
        }
        else
        {
            progress?.Report(content.Length);
        }

        return Task.CompletedTask;
    }
}
