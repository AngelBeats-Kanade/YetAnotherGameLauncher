using System.Text.Json;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Utilities;

namespace YetAnotherGameLauncher.Core.Services;

/// <summary>
/// 管理本地安装状态文件 {installDir}/.yagl/state.json。
/// 文件缺失或损坏时视为未安装（返回 null），由上层决定全量下载。
/// </summary>
public sealed class LocalStateService(string installDir)
{
    public const string StateDirName = ".yagl";

    private string StateFilePath => Path.Combine(installDir, StateDirName, "state.json");

    /// <summary>
    /// 读取状态；gameId/serverId 不匹配、文件损坏或不可读（被占用/权限）时同样视为未安装。
    /// 本方法处于 RefreshAsync 的 fire-and-forget 调用链上，任何异常都不允许穿出。
    /// </summary>
    public LocalGameState? Load(string gameId, string serverId)
    {
        if (!File.Exists(StateFilePath))
        {
            return null;
        }

        try
        {
            var state = JsonSerializer.Deserialize<LocalGameState>(
                File.ReadAllText(StateFilePath), Json.Default);
            return state is not null && state.GameId == gameId && state.ServerId == serverId
                ? state
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 损坏视为未安装（全量清单校验兜底）；被占用视为状态未知（下次刷新重读）
            return null;
        }
    }

    /// <summary>原子保存：先写临时文件再替换，避免写入中途崩溃损坏状态文件。</summary>
    public async Task SaveAsync(LocalGameState state, CancellationToken cancellationToken = default) =>
        await FileUtilities.WriteAtomicAsync(
            StateFilePath, JsonSerializer.Serialize(state, Json.Default), cancellationToken).ConfigureAwait(false);
}
