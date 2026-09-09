using YetAnotherGameLauncher.Core.Models;

namespace YetAnotherGameLauncher.Core.Services;

/// <summary>
/// 出站网络代理管理器：持有全局唯一的 <see cref="SocketsHttpHandler"/>，按设置切换代理；
/// HttpClient 可运行时改写 Proxy —— 设置保存后即时生效，无需重启（与限速的动态生效模式一致）。
/// </summary>
public sealed class NetworkProxyManager
{
    /// <summary>共享底层 handler：所有 HttpClient（版本/下载/背景/渠道）共用，保证代理一处生效。</summary>
    public SocketsHttpHandler Handler { get; } = new()
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(30),
    };

    /// <summary>按设置应用代理。</summary>
    /// <param name="settings">当前全局设置。</param>
    public void Apply(AppSettings settings)
    {
        switch (settings.ProxyMode)
        {
            case ProxyMode.None:
                Handler.Proxy = null;
                Handler.UseProxy = false;
                break;
            case ProxyMode.Manual when Uri.TryCreate(settings.ProxyAddress, UriKind.Absolute, out var proxy)
                && proxy.Scheme is "http" or "https":
                Handler.Proxy = new System.Net.WebProxy(proxy);
                Handler.UseProxy = true;
                break;
            default:
                // System：交给系统默认解析（UseProxy=true 且不指定 Proxy）
                Handler.Proxy = null;
                Handler.UseProxy = true;
                break;
        }
    }
}
