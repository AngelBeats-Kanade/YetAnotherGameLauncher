namespace YetAnotherGameLauncher.Core.Abstractions;

/// <summary>
/// 单文件差分应用抽象：把"旧文件 + 补丁文件 → 新文件"的逐文件差分能力与具体工具解耦
/// （终末地 v2 更新载荷里每条 <c>.chk_patch</c> 就是一个 HDiffPatch 单文件补丁）。
/// 目录模式见 <see cref="IPatchApplier"/>（鸣潮 krpdiff 目录差分）。
/// </summary>
public interface IFilePatchApplier
{
    /// <summary>把 <paramref name="patchFilePath"/> 应用到 <paramref name="oldFilePath"/> 产出
    /// <paramref name="newFilePath"/>（产物目录须由调用方保证存在）。</summary>
    Task ApplyFileAsync(
        string patchFilePath, string oldFilePath, string newFilePath, CancellationToken cancellationToken = default);
}
