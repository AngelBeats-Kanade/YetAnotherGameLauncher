using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using Xunit;

namespace YetAnotherGameLauncher.Core.Tests.Services;

/// <summary>代理管理器三态切换：System 走系统默认、None 直连、Manual 指定地址（非法地址退回 System）。</summary>
public class NetworkProxyManagerTests
{
    [Fact]
    public void Apply_System_FollowsSystemDefaults()
    {
        var manager = new NetworkProxyManager();

        manager.Apply(new AppSettings { ProxyMode = ProxyMode.System, ProxyAddress = "http://127.0.0.1:7890" });

        Assert.True(manager.Handler.UseProxy);
        Assert.Null(manager.Handler.Proxy); // 不指定代理 = 系统默认解析
    }

    [Fact]
    public void Apply_None_DisablesProxy()
    {
        var manager = new NetworkProxyManager();

        manager.Apply(new AppSettings { ProxyMode = ProxyMode.None });

        Assert.False(manager.Handler.UseProxy);
        Assert.Null(manager.Handler.Proxy);
    }

    [Fact]
    public void Apply_ManualWithValidAddress_UsesWebProxy()
    {
        var manager = new NetworkProxyManager();

        manager.Apply(new AppSettings { ProxyMode = ProxyMode.Manual, ProxyAddress = "http://127.0.0.1:7890" });

        Assert.True(manager.Handler.UseProxy);
        var proxy = Assert.IsType<System.Net.WebProxy>(manager.Handler.Proxy);
        Assert.Contains("127.0.0.1:7890", proxy.Address?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_ManualWithInvalidAddress_FallsBackToSystem()
    {
        var manager = new NetworkProxyManager();

        manager.Apply(new AppSettings { ProxyMode = ProxyMode.Manual, ProxyAddress = "not-a-proxy" });

        Assert.True(manager.Handler.UseProxy);
        Assert.Null(manager.Handler.Proxy);
    }
}
