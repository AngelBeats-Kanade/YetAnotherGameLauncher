namespace YetAnotherGameLauncher.Core.Services;

using YetAnotherGameLauncher.Core.Abstractions;

/// <summary>
/// IPlatformInfo 选择工厂：按当前 OS 落到对应实现，全仓只此一处分支
/// （DI 组合根与各 ViewModel 缺省参数共用）。产品当前只声明 Linux + Windows 双平台：
/// 其它 OS（macOS）会落到 Windows 实现作为缺省，将来增补新平台只改这里。
/// </summary>
public static class PlatformInfoFactory
{
    /// <summary>创建与当前操作系统匹配的平台环境实现。</summary>
    public static IPlatformInfo Create() =>
        OperatingSystem.IsLinux() ? new LinuxPlatformInfo() : new WindowsPlatformInfo();
}
