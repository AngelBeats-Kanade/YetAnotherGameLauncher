using YetAnotherGameLauncher.Core.Abstractions;

namespace YetAnotherGameLauncher.TestSupport;

/// <summary>
/// 平台信息假实现：IsLinux 与 GPU 厂商探测结果可控，
/// 让测试在任意 OS 上确定性地走指定平台分支。
/// </summary>
public sealed class FakePlatformInfo(
    bool isLinux,
    bool nvidiaGpuPresent = false,
    bool amdGpuPresent = false,
    bool intelGpuPresent = false) : IPlatformInfo
{
    /// <summary>OpenDirectoryInFileManager 收到的路径（按调用顺序），供断言。</summary>
    public List<string> OpenedPaths { get; } = [];

    /// <summary>是否按 Linux 平台处理（决定兼容层 UI、Proton 推荐与 XDG 行为）。</summary>
    public bool IsLinux => isLinux;

    /// <summary>NVIDIA 显卡探测结果（影响 Proton 推荐环境变量与 NVDEC 选择）。</summary>
    public bool IsNvidiaGpuPresent => nvidiaGpuPresent;

    /// <summary>探测到的 GPU 厂商集合（与各布尔参数一一对应，固定排序保证断言确定性）。</summary>
    public IReadOnlyList<GpuVendor> GpuVendors
    {
        get
        {
            var vendors = new List<GpuVendor>();
            if (nvidiaGpuPresent)
            {
                vendors.Add(GpuVendor.Nvidia);
            }

            if (amdGpuPresent)
            {
                vendors.Add(GpuVendor.Amd);
            }

            if (intelGpuPresent)
            {
                vendors.Add(GpuVendor.Intel);
            }

            return vendors;
        }
    }

    /// <summary>记录打开目录请求，不触达真实文件管理器。</summary>
    public void OpenDirectoryInFileManager(string path) => OpenedPaths.Add(path);
}
