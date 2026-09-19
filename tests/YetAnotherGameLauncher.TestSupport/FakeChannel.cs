using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;

namespace YetAnotherGameLauncher.TestSupport;

/// <summary>假渠道：可配置版本信息与清单，供渠道编排测试复用。</summary>
public sealed class FakeChannel : IGameChannelApi
{
    public ChannelVersionInfo VersionInfo { get; set; } = new() { LatestVersion = "2.0.0" };

    public Dictionary<string, GameManifest> Manifests { get; } = new(StringComparer.Ordinal);

    public Dictionary<(string From, string To), GameManifest> IncrementalManifests { get; } = new();

    public GameManifest? PredownloadManifest { get; set; }

    /// <summary>版本检测注入的异常（非 null 时 GetVersionInfoAsync 抛出；模拟断网/服务端错误）。</summary>
    public Exception? VersionInfoError { get; set; }

    /// <summary>清单拉取注入的异常（非 null 时 GetManifestAsync 抛出）。</summary>
    public Exception? ManifestError { get; set; }

    /// <summary>增量清单拉取注入的异常（非 null 时 GetIncrementalManifestAsync 抛出）。</summary>
    public Exception? IncrementalManifestError { get; set; }

    /// <summary>预下载清单拉取注入的异常（非 null 时 GetPredownloadManifestAsync 抛出）。</summary>
    public Exception? PredownloadManifestError { get; set; }

    public List<string> ManifestRequests { get; } = [];

    /// <summary>版本检测调用次数（按调用顺序记录请求的服务器 id；验证"每启动每服务器只检测一次"用）。</summary>
    public List<string> VersionInfoRequests { get; } = [];

    public Task<ChannelVersionInfo> GetVersionInfoAsync(GameServer server, CancellationToken cancellationToken = default)
    {
        VersionInfoRequests.Add(server.Id);
        return VersionInfoError is { } versionError
            ? Task.FromException<ChannelVersionInfo>(versionError)
            : Task.FromResult(VersionInfo);
    }

    public Task<GameManifest> GetManifestAsync(GameServer server, string version, CancellationToken cancellationToken = default)
    {
        ManifestRequests.Add(version);
        return ManifestError is { } manifestError
            ? Task.FromException<GameManifest>(manifestError)
            : Task.FromResult(Manifests[version]);
    }

    public Task<GameManifest?> GetIncrementalManifestAsync(
        GameServer server, string fromVersion, string toVersion, CancellationToken cancellationToken = default)
    {
        if (IncrementalManifestError is { } incrementalError)
        {
            return Task.FromException<GameManifest?>(incrementalError);
        }

        return Task.FromResult<GameManifest?>(IncrementalManifests.TryGetValue((fromVersion, toVersion), out var manifest)
            ? manifest
            : null);
    }

    public Task<GameManifest?> GetPredownloadManifestAsync(GameServer server, CancellationToken cancellationToken = default)
    {
        if (PredownloadManifestError is { } predownloadError)
        {
            return Task.FromException<GameManifest?>(predownloadError);
        }

        return Task.FromResult(PredownloadManifest);
    }
}
