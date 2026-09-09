using System.Text;
using YetAnotherGameLauncher.Channels.Kuro;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.TestSupport;
using Xunit;

namespace YetAnotherGameLauncher.Channels.Kuro.Tests;

/// <summary>
/// 鸣潮唤取记录服务：日志 XOR 解密与 URL 提取、查询请求体/游标翻页、本地缓存合并。
/// </summary>
public class KuroGachaServiceTests : IDisposable
{
    private readonly TempDir _tempDir = new();
    private readonly StubHttpHandler _handler = new();

    private KuroGachaService CreateService() =>
        new(new HttpClient(_handler), _tempDir.FilePath("gacha"));

    public void Dispose() => _tempDir.Dispose();

    private const string SampleUrl = "https://aki-gm-resources.aki-game.com/aki/gacha/index.html" +
        "#/record?svr_id=76xx&player_id=100000002&lang=zh-Hans&gacha_id=xx&gacha_type=1" +
        "&svr_area=cn&record_id=3dc48935abc&resources_id=0bd52af4&platform=PC";

    /// <summary>与官方混淆互逆：奇数索引 ^0xA5、偶数 ^0xEF，前缀 magic 三字节。</summary>
    private static byte[] EncryptLog(string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        var data = new byte[payload.Length + 3];
        data[0] = 0xA5;
        data[1] = 0xEF;
        data[2] = 0xA5;
        for (var i = 0; i < payload.Length; i++)
        {
            data[i + 3] = (byte)(payload[i] ^ (i % 2 == 1 ? 0xA5 : 0xEF));
        }

        return data;
    }

    [Fact]
    public void TryExtractGachaUrl_DecryptedLog_ParsesAllParams()
    {
        var installDir = _tempDir.FilePath("Wuthering Waves Game");
        Directory.CreateDirectory(Path.Combine(installDir, "Client", "Saved", "Logs"));
        File.WriteAllBytes(
            Path.Combine(installDir, "Client", "Saved", "Logs", "Client.log"),
            EncryptLog($"2026-09-09 12:00:00 info {SampleUrl}\nother line"));

        var info = CreateService().TryExtractGachaUrl(installDir);

        Assert.NotNull(info);
        Assert.Equal("76xx", info!.ServerId);
        Assert.Equal("100000002", info.PlayerId);
        Assert.Equal("zh-Hans", info.LanguageCode);
        Assert.Equal("3dc48935abc", info.RecordId);
        Assert.True(info.IsChina);
    }

    [Fact]
    public void TryExtractGachaUrl_PlaintextLog_AlsoExtracted()
    {
        var installDir = _tempDir.FilePath("WW");
        Directory.CreateDirectory(Path.Combine(installDir, "Saved", "Logs"));
        File.WriteAllText(
            Path.Combine(installDir, "Saved", "Logs", "Client.log"),
            $"url=\"{SampleUrl}\"");

        var info = CreateService().TryExtractGachaUrl(installDir);

        Assert.NotNull(info);
        Assert.Equal("100000002", info!.PlayerId);
    }

    [Fact]
    public void TryExtractGachaUrl_NoUrl_ReturnsNull()
    {
        var installDir = _tempDir.FilePath("WW");
        Directory.CreateDirectory(installDir);

        Assert.Null(CreateService().TryExtractGachaUrl(installDir));
        Assert.Null(CreateService().TryExtractGachaUrl(null));
    }

    [Fact]
    public async Task FetchPoolAsync_BuildsRequestBodyAndStopsOnShortPage()
    {
        _handler.Map("https://gmserver-api.aki-game2.com/gacha/record/query", """
            {"code":0,"data":[
              {"time":"2026-09-01 10:00:00","name":"Trailblaze","qualityLevel":5},
              {"time":"2026-09-01 09:00:00","name":"Luster","qualityLevel":4}
            ]}
            """);

        var info = new GachaUrlInfo("76xx", "100000002", "zh-Hans", "rec123", IsChina: true);
        var records = await CreateService().FetchPoolAsync(info, 1);

        Assert.Equal(2, records.Count);
        Assert.Equal("Trailblaze", records[0].Name);
        Assert.Equal(5, records[0].QualityLevel);
        Assert.Equal(1, records[0].PoolType);

        // 请求体：官方六字段（cardPoolId 与 recordId 同为 record_id），无鉴权头
        var request = Assert.Single(_handler.Requests);
        Assert.Equal("https://gmserver-api.aki-game2.com/gacha/record/query",
            request.RequestUri!.ToString());
        var body = await request.Content!.ReadAsStringAsync();
        Assert.Contains("\"cardPoolId\":\"rec123\"", body);
        Assert.Contains("\"cardPoolType\":\"1\"", body);
        Assert.Contains("\"playerId\":\"100000002\"", body);
        Assert.Contains("\"serverId\":\"76xx\"", body);
        Assert.DoesNotContain("recordIdCursor", body); // 首页请求不带游标
    }

    [Fact]
    public async Task FetchPoolAsync_InternationalDomain_UsesNet()
    {
        _handler.Map("https://gmserver-api.aki-game2.net/gacha/record/query", """{"code":0,"data":[]}""");
        var info = new GachaUrlInfo("usa", "1", "en-us", "rec", IsChina: false);

        await CreateService().FetchPoolAsync(info, 3);

        await _handler.Requests[0].Content!.ReadAsStringAsync(); // body 无关
        Assert.Equal("https://gmserver-api.aki-game2.net/gacha/record/query",
            _handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public void MergeAndSave_DeduplicatesAndSortsByTime()
    {
        var service = CreateService();
        service.MergeAndSave(
        [
            new("2026-09-01 10:00:00", "A", 5, 1),
            new("2026-09-02 10:00:00", "B", 4, 2),
        ]);
        service.MergeAndSave(
        [
            // 与缓存重复（时间+名字+稀有度+池 相同）→ 不重复
            new("2026-09-01 10:00:00", "A", 5, 1),
            new("2026-08-01 10:00:00", "C", 4, 3),
        ]);

        var cached = service.LoadCached();

        Assert.Equal(3, cached.Count);
        Assert.Equal("B", cached[0].Name); // 按时间倒序
        Assert.Equal("C", cached[2].Name);
    }

    [Fact]
    public void LoadCached_MissingFile_ReturnsEmpty()
    {
        Assert.Empty(CreateService().LoadCached());
    }
}
