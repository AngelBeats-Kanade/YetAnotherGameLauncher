using System.ComponentModel;

namespace YetAnotherGameLauncher.Services;

/// <summary>界面文案本地化服务：键取值 + 参数格式化 + 语言切换广播。</summary>
public interface ILocalizationService : INotifyPropertyChanged
{
    /// <summary>语言设置值："system"（跟随系统）/ "zh-CN" / "en-US"。</summary>
    string Language { get; }

    /// <summary>当前生效的 culture 名（system 已解析为具体语言）。</summary>
    string EffectiveCulture { get; }

    /// <summary>取文案；缺键回退默认语言，仍缺失返回键本身（便于发现漏译）。</summary>
    string this[string key] { get; }

    /// <summary>带参数格式化；无参数时原样返回（避免 {exe} 之类非格式槽被 string.Format 误解析）。</summary>
    string Format(string key, params object?[] args);

    /// <summary>切换语言并广播 Item[] 刷新；写入 settings.language 的值即此处的 language。</summary>
    void SetLanguage(string language);
}
