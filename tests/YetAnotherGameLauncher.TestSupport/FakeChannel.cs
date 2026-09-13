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

    public List<string> ManifestRequests { get; } = [];

    /// <summary>版本检测调用次数（按调用顺序记录请求的服务器 id；验证"每启动每服务器只检测一次"用）。</summary>
    public List<string> VersionInfoRequests { get; } = [];

    public Task<ChannelVersionInfo> GetVersionInfoAsync(GameServer server, CancellationToken cancellationToken = default)
    {
        VersionInfoRequests.Add(server.Id);
        return Task.FromResult(VersionInfo);
    }

    public Task<GameManifest> GetManifestAsync(GameServer server, string version, CancellationToken cancellationToken = default)
    {
        ManifestRequests.Add(version);
        return Task.FromResult(Manifests[version]);
    }

    public Task<GameManifest?> GetIncrementalManifestAsync(
        GameServer server, string fromVersion, string toVersion, CancellationToken cancellationToken = default) =>
        Task.FromResult<GameManifest?>(IncrementalManifests.TryGetValue((fromVersion, toVersion), out var manifest)
            ? manifest
            : null);

    public Task<GameManifest?> GetPredownloadManifestAsync(GameServer server, CancellationToken cancellationToken = default) =>
        Task.FromResult(PredownloadManifest);
}
