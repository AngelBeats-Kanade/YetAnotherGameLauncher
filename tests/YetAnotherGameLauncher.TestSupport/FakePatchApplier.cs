using YetAnotherGameLauncher.Core.Abstractions;

namespace YetAnotherGameLauncher.TestSupport;

/// <summary>假补丁器：按"补丁文件名 → 目标相对路径 → 内容"的预设生成 newDir 输出，可模拟失败与坏输出。</summary>
public sealed class FakePatchApplier : IPatchApplier
{
    /// <summary>key = 补丁文件名（不含目录），value = 该组生成的新文件（相对路径 → 内容）。</summary>
    public Dictionary<string, Dictionary<string, byte[]>> Outputs { get; } = new(StringComparer.Ordinal);

    public List<(string PatchFile, List<string> OldFiles)> Calls { get; } = [];

    public int FailOnCallIndex { get; set; } = -1;

    public bool CorruptOutput { get; set; }

    public Task ApplyAsync(string patchFilePath, string oldDir, string newDir, CancellationToken cancellationToken = default)
    {
        var index = Calls.Count;
        Calls.Add((
            Path.GetFileName(patchFilePath),
            [.. Directory.EnumerateFiles(oldDir, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(oldDir, f).Replace('\\', '/'))
                .OrderBy(p => p, StringComparer.Ordinal)]));

        if (index == FailOnCallIndex)
        {
            throw new IOException("假补丁失败");
        }

        var outputs = Outputs[Path.GetFileName(patchFilePath)];
        Directory.CreateDirectory(newDir);
        foreach (var (relativePath, content) in outputs)
        {
            var target = Path.Combine(newDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, CorruptOutput ? [0xFF, 0xFF] : content);
        }

        return Task.CompletedTask;
    }
}
