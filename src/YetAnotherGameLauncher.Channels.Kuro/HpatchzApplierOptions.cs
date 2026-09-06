namespace YetAnotherGameLauncher.Channels.Kuro;

/// <summary>HpatchzApplier 的可配置项。</summary>
public sealed class HpatchzApplierOptions
{
    /// <summary>hpatchz 可执行文件路径；默认从 PATH 解析。渠道配置可指向随启动器分发的原生二进制。</summary>
    public string HpatchzPath { get; set; } = "hpatchz";

    /// <summary>单次补丁应用超时（毫秒）。</summary>
    public int TimeoutMilliseconds { get; set; } = 600_000;
}
