using YetAnotherGameLauncher.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace YetAnotherGameLauncher.Channels.Hypergryph;

public static class HypergryphServiceCollectionExtensions
{
    /// <summary>games.json 中 game.channel 对应的渠道键。</summary>
    public const string ChannelKey = "hypergryph";

    /// <summary>注册 GRYPHLINE 渠道实现（按渠道键的 keyed service）。</summary>
    public static IServiceCollection AddHypergryphChannel(this IServiceCollection services)
    {
        services.AddHttpClient(ChannelKey, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("YetAnotherGameLauncher/0.1");
        });

        services.AddKeyedTransient<IGameChannelApi>(ChannelKey, (serviceProvider, _) =>
            new GryphlineChannelApi(
                serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(ChannelKey),
                serviceProvider.GetService<ILogger>()));

        return services;
    }
}
