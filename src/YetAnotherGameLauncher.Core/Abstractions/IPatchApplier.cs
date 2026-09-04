namespace YetAnotherGameLauncher.Core.Abstractions;

/// <summary>
/// 差分补丁应用器抽象。实现方（如 HpatchzApplier）负责对 oldDir 应用补丁文件，
/// 生成 newDir 中的新文件。oldDir/newDir 的内部布局由调用方与实现约定
/// （鸣潮渠道：按 dest 相对路径布局，与 krpdiff 目录模式一致）。
/// </summary>
public interface IPatchApplier
{
    Task ApplyAsync(string patchFilePath, string oldDir, string newDir, CancellationToken cancellationToken = default);
}
