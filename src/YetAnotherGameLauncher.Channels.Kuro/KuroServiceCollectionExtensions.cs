using YetAnotherGameLauncher.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace YetAnotherGameLauncher.Channels.Kuro;

public static class KuroServiceCollectionExtensions
{
    /// <summary>games.json 中 game.channel 对应的渠道键。</summary>
    public const string ChannelKey = "kuro";

    /// <summary>注册库洛渠道实现（按渠道键的 keyed service）。</summary>
    public static IServiceCollection AddKuroChannel(this IServiceCollection services)
    {
        services.AddKeyedSingleton<IGameChannelApi, KuroChannelApi>(ChannelKey);
        return services;
    }
}
