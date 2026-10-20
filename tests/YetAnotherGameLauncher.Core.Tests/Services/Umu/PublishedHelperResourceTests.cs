using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;
using YetAnotherGameLauncher.Core.Services.Umu;

namespace YetAnotherGameLauncher.Core.Tests.Services.Umu;

/// <summary>
/// 发布物内嵌资源检查（2026-10-20 复审）：CI 的 publish smoke 通过环境变量指向**发布后的**
/// Core.dll，直接读取 PE ManifestResource 并断言 helper 字节以 MZ 开头。不能用字符串 grep
/// 代替——Core.dll 里本来就有 "yagl-parent-bridge" 字面量（资源名/常量），资源缺失时 grep 仍绿。
/// </summary>
public sealed class PublishedHelperResourceTests
{
    [Fact]
    public void PublishedCore_EmbedsHelperAsPeResource()
    {
        var path = Environment.GetEnvironmentVariable("YAGL_PUBLISHED_CORE");
        if (string.IsNullOrWhiteSpace(path))
        {
            Assert.Skip("发布物检查仅在 CI publish smoke 注入 YAGL_PUBLISHED_CORE 时运行");
        }

        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();

        ManifestResource? resource = null;
        foreach (var handle in metadata.ManifestResources)
        {
            var candidate = metadata.GetManifestResource(handle);
            if (string.Equals(
                    metadata.GetString(candidate.Name),
                    DecoyParentBridge.HelperFileName,
                    StringComparison.Ordinal))
            {
                resource = candidate;
                break;
            }
        }

        Assert.NotNull(resource);
        var corHeader = pe.PEHeaders.CorHeader;
        Assert.NotNull(corHeader);
        var resources = pe.GetSectionData(corHeader!.ResourcesDirectory.RelativeVirtualAddress);
        var offset = checked((int)resource.Value.Offset);
        var reader = resources.GetReader(offset, resources.Length - offset);
        var length = reader.ReadInt32();
        Assert.True(length > 0, "内嵌 helper 资源长度为 0");
        var bytes = reader.ReadBytes(length);

        Assert.Equal((byte)'M', bytes[0]);
        Assert.Equal((byte)'Z', bytes[1]);
    }
}
