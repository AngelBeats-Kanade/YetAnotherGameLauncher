using System.IO.Compression;

namespace YetAnotherGameLauncher.TestSupport;

/// <summary>测试用 zip 压缩包构造。</summary>
public static class TestZip
{
    public static byte[] Create(params (string EntryPath, byte[] Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (entryPath, content) in entries)
            {
                var entry = archive.CreateEntry(entryPath);
                using var entryStream = entry.Open();
                entryStream.Write(content);
            }
        }

        return stream.ToArray();
    }

    public static byte[] Create(params (string EntryPath, string Content)[] entries) =>
        Create([.. entries.Select(e => (e.EntryPath, System.Text.Encoding.UTF8.GetBytes(e.Content)))]);
}
