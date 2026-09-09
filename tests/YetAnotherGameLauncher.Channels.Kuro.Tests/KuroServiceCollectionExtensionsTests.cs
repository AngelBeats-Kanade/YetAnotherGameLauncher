using YetAnotherGameLauncher.Channels.Kuro;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace YetAnotherGameLauncher.Channels.Kuro.Tests;

/// <summary>渠道 DI 注册：AddKuroChannel 后按渠道键可解析出 Kuro 实现。</summary>
public class KuroServiceCollectionExtensionsTests
{
    [Fact]
    public void AddKuroChannel_RegistersKeyedApi()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Core.Abstractions.IDownloader>(new YetAnotherGameLauncher.TestSupport.FakeDownloader());
        services.AddKuroChannel();

        using var provider = services.BuildServiceProvider();
        var api = provider.GetRequiredKeyedService<IGameChannelApi>(KuroServiceCollectionExtensions.ChannelKey);

        Assert.IsType<KuroChannelApi>(api);
    }

    private sealed class MinimalApi : IGameChannelApi
    {
        public Task<ChannelVersionInfo> GetVersionInfoAsync(GameServer server, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<GameManifest> GetManifestAsync(GameServer server, string version, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<GameManifest?> GetIncrementalManifestAsync(
            GameServer server, string fromVersion, string toVersion, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task GetPredownloadManifestAsync_DefaultImplementation_ReturnsNull()
    {
        // 文件式渠道（接口默认实现）不提供包式预下载清单
        Assert.Null(await ((IGameChannelApi)new MinimalApi()).GetPredownloadManifestAsync(new GameServer { Id = "cn", Name = "CN" }));
    }
}
