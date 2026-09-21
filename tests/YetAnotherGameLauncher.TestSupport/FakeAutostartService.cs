using YetAnotherGameLauncher.Core.Services;

namespace YetAnotherGameLauncher.TestSupport;

/// <summary>
/// 自启动服务假实现：启用状态可编程、切换可失败，供 ViewModel 层
/// （MainWindowViewModel.Get/SetAutostartAsync）行为测试注入。
/// </summary>
public sealed class FakeAutostartService : IAutostartService
{
    /// <summary>当前启用状态（IsEnabledAsync 的返回值）。</summary>
    public bool Enabled { get; set; }

    /// <summary>SetEnabledAsync 成功后是否把 Enabled 同步为目标值（false = 模拟"写入成功但状态没变"，如组策略拦截）。</summary>
    public bool ApplyEnabledOnSet { get; set; } = true;

    /// <summary>非 null 时 SetEnabledAsync 抛该异常（模拟注册表/桌面入口写入失败）。</summary>
    public Exception? SetEnabledFailure { get; set; }

    /// <summary>SetEnabledAsync 收到的参数序列（按调用顺序），供断言。</summary>
    public List<bool> SetCalls { get; } = [];

    /// <inheritdoc />
    public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default) => Task.FromResult(Enabled);

    /// <inheritdoc />
    public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        SetCalls.Add(enabled);
        if (SetEnabledFailure is { } failure)
        {
            throw failure;
        }

        if (ApplyEnabledOnSet)
        {
            Enabled = enabled;
        }

        return Task.CompletedTask;
    }
}
