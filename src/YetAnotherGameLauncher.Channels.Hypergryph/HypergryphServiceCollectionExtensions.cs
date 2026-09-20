using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Services;

namespace YetAnotherGameLauncher.Channels.Hypergryph;

public static class HypergryphServiceCollectionExtensions
{
    /// <summary>games.json 中 game.channel 对应的渠道键。</summary>
    public const string ChannelKey = "hypergryph";

    /// <summary>注册 GRYPHLINE 渠道实现（按渠道键的 keyed service）。</summary>
    public static IServiceCollection AddHypergryphChannel(this IServiceCollection services)
    {
        // Primary 即全局共享 SocketsHttpHandler（NetworkProxyManager 持有、代理热改由 Apply 承担），
        // 寿命必须设为无限：默认 2 分钟轮换会让工厂在链过期清理时 Dispose 共享 handler，
        // 全应用网络随之死亡（2026-09-20 复审修复）
        services.AddHttpClient(ChannelKey, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("YetAnotherGameLauncher/0.1");
        })
        .ConfigurePrimaryHttpMessageHandler(sp =>
            sp.GetRequiredService<NetworkProxyManager>().Handler)
        .SetHandlerLifetime(Timeout.InfiniteTimeSpan);

        services.AddKeyedTransient<IGameChannelApi>(ChannelKey, (serviceProvider, _) =>
            new GryphlineChannelApi(
                serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(ChannelKey),
                serviceProvider.GetService<ILogger>()));

        return services;
    }
}
