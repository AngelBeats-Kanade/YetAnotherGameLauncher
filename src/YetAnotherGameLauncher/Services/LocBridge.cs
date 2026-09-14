namespace YetAnotherGameLauncher.Services;

/// <summary>
/// 静态桥：DI 组合根在启动时把本地化服务实例注册到此（MainWindowViewModel 构造函数）。
/// 消费方是 LaunchSettingsViewModel 的 LaunchModes 属性初始化器——它在构造函数体之前求值，
/// 实例 <c>_loc</c> 尚不可用，只能走静态桥；测试经 VmFactory 替换 Instance 保证文案确定性。
/// </summary>
public static class LocBridge
{
    /// <summary>当前本地化服务实例（应用启动时由 DI 组合根赋值）。</summary>
    public static ILocalizationService Instance { get; set; } = new LocalizationService();
}
