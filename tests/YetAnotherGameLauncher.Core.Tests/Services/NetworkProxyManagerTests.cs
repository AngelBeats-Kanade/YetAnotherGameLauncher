using Xunit;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;

namespace YetAnotherGameLauncher.Core.Tests.Services;

/// <summary>代理管理器三态切换：System 走系统默认、None 直连、Manual 指定地址（非法地址按直连处理并告警，F75）。</summary>
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
    public void Apply_ManualWithInvalidAddress_FallsBackToDirectNotSystem()
    {
        // F75 改判（原"退回 System"契约废弃）：Manual 且地址非法是用户意图不可读——
        // 静默漂移成"跟随系统"是意图的反面（配置代理通常正是为了绕开系统代理），
        // 按直连处理；SaveAsync 校验拦新写入，此处只兜存量坏值/手改
        var manager = new NetworkProxyManager();

        manager.Apply(new AppSettings { ProxyMode = ProxyMode.Manual, ProxyAddress = "not-a-proxy" });

        Assert.False(manager.Handler.UseProxy);
        Assert.Null(manager.Handler.Proxy);
    }
}
