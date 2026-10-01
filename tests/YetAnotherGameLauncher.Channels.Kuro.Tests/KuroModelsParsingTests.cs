using System.Text.Json;
using Xunit;
using YetAnotherGameLauncher.Channels.Kuro.Models;

namespace YetAnotherGameLauncher.Channels.Kuro.Tests;

/// <summary>
/// Kuro 增量清单模型的解析保数据测试（2026-10-02 真机契约）：groupInfos 条目仅
/// dest/srcFiles/dstFiles 三键（组层无 size/md5/fromFolder）；dstFiles 条目可携带
/// chunkInfos（start/end/md5 三键）；顶层 deleteFiles 为字符串数组。
/// </summary>
public class KuroModelsParsingTests
{
    [Fact]
    public void IndexFile_ParsesChunkInfosAndGroupShape()
    {
        // 真机 3.6.1→3.7.0 增量清单形态（2026-10-02 实测节选）：dstFiles 带 chunkInfos、
        // 组层无 size/md5；chunkInfos 仅建模解析（校验链按整文件 size+MD5），防止字段拼错
        // 在未来接消费方时静默丢数据。
        const string json = """
            {
              "resource": [],
              "deleteFiles": [ "Client/Content/Paks/old.pak" ],
              "groupInfos": [
                {
                  "dest": "3.6.1_3.7.0_group_0_token.krpdiff",
                  "srcFiles": [ { "dest": "Client/Content/Paks/old.pak", "md5": "99999999999999999999999999999999", "size": 4 } ],
                  "dstFiles": [
                    {
                      "dest": "Client/Content/HD/new.pak", "md5": "12121212121212121212121212121212", "size": 5,
                      "chunkInfos": [ { "start": 0, "end": 104857599, "md5": "4cc9e076b5841dc3ac66c5f04526f09e" } ]
                    }
                  ]
                }
              ]
            }
            """;

        var parsed = JsonSerializer.Deserialize<KuroIndexFile>(json);

        Assert.NotNull(parsed);
        Assert.Equal(["Client/Content/Paks/old.pak"], parsed.DeleteFiles);
        var group = Assert.Single(parsed.GroupInfos!);
        Assert.Equal(0, group.Size); // 组层无 size 键（真机形态），默认 0——size 由渠道层回填
        var dst = Assert.Single(group.DstFiles);
        var chunk = Assert.Single(dst.ChunkInfos!);
        Assert.Equal(0, chunk.Start);
        Assert.Equal(104857599, chunk.End);
        Assert.Equal("4cc9e076b5841dc3ac66c5f04526f09e", chunk.Md5);
    }
}
