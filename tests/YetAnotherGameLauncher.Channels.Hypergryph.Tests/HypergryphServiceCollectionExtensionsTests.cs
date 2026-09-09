using YetAnotherGameLauncher.Channels.Hypergryph;
using YetAnotherGameLauncher.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace YetAnotherGameLauncher.Channels.Hypergryph.Tests;

/// <summary>渠道 DI 注册：AddHypergryphChannel 后按渠道键可解析出 GRYPHLINE 实现。</summary>
public class HypergryphServiceCollectionExtensionsTests
{
    [Fact]
    public void AddHypergryphChannel_RegistersKeyedApiWithHttpClient()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Core.Services.NetworkProxyManager>();
        services.AddHypergryphChannel();

        using var provider = services.BuildServiceProvider();
        var api = provider.GetRequiredKeyedService<IGameChannelApi>(HypergryphServiceCollectionExtensions.ChannelKey);

        Assert.IsType<GryphlineChannelApi>(api);
    }
}
