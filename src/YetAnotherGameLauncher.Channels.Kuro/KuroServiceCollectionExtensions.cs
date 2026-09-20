using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YetAnotherGameLauncher.Core.Abstractions;

namespace YetAnotherGameLauncher.Channels.Kuro;

public static class KuroServiceCollectionExtensions
{
    /// <summary>games.json 中 game.channel 对应的渠道键。</summary>
    public const string ChannelKey = "kuro";

    /// <summary>注册库洛渠道实现（按渠道键的 keyed service）。显式工厂传类型化 logger：
    /// 裸 ILogger 不在容器里，类型激活会让可注入 logger 落到默认 null（2026-09-20 复审修复）。</summary>
    public static IServiceCollection AddKuroChannel(this IServiceCollection services)
    {
        services.AddKeyedSingleton<IGameChannelApi>(ChannelKey, (sp, _) => new KuroChannelApi(
            sp.GetRequiredService<IDownloader>(),
            logger: sp.GetRequiredService<ILoggerFactory>().CreateLogger<KuroChannelApi>()));
        return services;
    }
}
