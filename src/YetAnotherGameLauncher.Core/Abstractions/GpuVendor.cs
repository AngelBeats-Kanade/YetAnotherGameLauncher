namespace YetAnotherGameLauncher.Core.Abstractions;

/// <summary>GPU 厂商（Linux 兼容层的启动环境与视频硬解路径都依赖它）。</summary>
public enum GpuVendor
{
    /// <summary>NVIDIA（闭源驱动经 /proc 探测，供 DXVK-NVAPI / NVDEC 判断）。</summary>
    Nvidia,

    /// <summary>AMD（Mesa/RADV，视频硬解走 VAAPI）。</summary>
    Amd,

    /// <summary>Intel（Mesa/ANV，视频硬解走 VAAPI）。</summary>
    Intel,
}
