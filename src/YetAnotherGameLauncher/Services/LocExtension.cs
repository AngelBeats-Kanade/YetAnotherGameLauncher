using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace YetAnotherGameLauncher.Services;

/// <summary>
/// 静态桥：DI 创建的本地化服务在此注册，供 LocExtension 的绑定作为 Source。
/// （Avalonia 绑定引擎对 "属性.索引器" 组合路径求值失败，故让绑定直接以服务为 Source、
/// 只走单级索引器 —— 该形式已由诊断测试验证。）
/// </summary>
public static class LocBridge
{
    public static ILocalizationService Instance { get; set; } = new LocalizationService();
}

/// <summary>XAML 用法：{svc:Loc settings_title} → 取当前语言文案，语言切换时随 Item[] 通知刷新。</summary>
public class LocExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider)
        => new Binding($"[{Key}]") { Source = LocBridge.Instance, Mode = BindingMode.OneWay };
}
