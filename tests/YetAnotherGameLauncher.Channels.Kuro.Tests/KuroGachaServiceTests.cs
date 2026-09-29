using System.Text;
using Xunit;
using YetAnotherGameLauncher.Core;
using YetAnotherGameLauncher.TestSupport;

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

    [Fact]
    public void CacheDirectory_LivesUnderDataDirectory_NotConfigDirectory()
    {
        // 2026-09-22 路径策略：唤取记录缓存（可重建）归数据目录，与背景/图标/ffmpeg 缓存同批迁移；
        // 配置目录只留 games.json 等不可清理物
        var service = new KuroGachaService(new HttpClient(_handler));
        Assert.Equal(Path.Combine(AppPaths.DataDirectory, "gacha"), service.CacheDirectory);
        Assert.False(service.CacheDirectory.StartsWith(
            AppPaths.ConfigDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    [Fact]
    public async Task MergeAndSave_ConcurrentDistinctRecords_BothSurvive()
    {
        // F62 并发半：两个 MergeAndSave 的读-改-写交错会丢更新——A 载入空缓存后停在写前，
        // B 载入同样为空、A 写入 {r1} 后 B 再写 {r2} 把 r1 冲掉（B 的载入发生在 A 写之前）。
        // 修复 = Load→Merge→Write 全程持跨实例串行锁：B 在锁上等 A，载入时已含 {r1}
        var recordA = new GachaRecord("2026-09-29 10:00:00", "角色A", 5, 1);
        var recordB = new GachaRecord("2026-09-29 11:00:00", "角色B", 4, 2);
        var parkedA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parkedB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serviceA = CreateService();
        var serviceB = CreateService();
        serviceA.BeforeSaveHookForTests = async () => { parkedA.TrySetResult(); await releaseA.Task; };
        serviceB.BeforeSaveHookForTests = async () => { parkedB.TrySetResult(); await releaseB.Task; };

        var taskA = Task.Run(() => serviceA.MergeAndSave([recordA]));
        await parkedA.Task.WaitAsync(TimeSpan.FromSeconds(5)); // A 载入空缓存后停在写前
        var taskB = Task.Run(() => serviceB.MergeAndSave([recordB]));
        // 修复形态：B 在锁上等 A，300ms 内不会到达写前钩子；缺陷形态：B 与 A 一样停写
        await parkedB.Task.WaitAsync(TimeSpan.FromMilliseconds(300));
        releaseA.TrySetResult();
        await taskA.WaitAsync(TimeSpan.FromSeconds(5));
        releaseB.TrySetResult();
        await taskB.WaitAsync(TimeSpan.FromSeconds(5));

        var cached = serviceB.LoadCached();
        Assert.Contains(cached, r => r.Name == "角色A");
        Assert.Contains(cached, r => r.Name == "角色B");
    }

    [Fact]
    public void MergeAndSave_WriteFailure_NoTempResidue()
    {
        // F62 残留半：WriteAllText 成功后 Move 失败（缓存落点被同名目录占用）不删 .tmp——
        // 带 Guid 的残留在缓存目录累积。修复 = WriteAtomicAsync 接管落盘（失败路径统一清理）
        var cacheDir = _tempDir.FilePath("gacha");
        Directory.CreateDirectory(Path.Combine(cacheDir, "wuthering-waves.json")); // 占住缓存落点

        CreateService().MergeAndSave([new GachaRecord("2026-09-29 10:00:00", "角色A", 5, 1)]);

        Assert.Empty(Directory.EnumerateFiles(cacheDir, "*.tmp"));
    }

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
    public void TryExtractGachaUrl_FirstCandidateLocked_FallsBackToNextCandidate()
    {
        // 审计缺口（2026-09-19）：游戏运行中日志被占用（IOException）→ 跳过该候选继续找下一个落盘位置
        var installDir = _tempDir.FilePath("WW");
        var lockedPath = Path.Combine(installDir, "Client", "Saved", "Logs", "Client.log");
        Directory.CreateDirectory(Path.GetDirectoryName(lockedPath)!);
        File.WriteAllBytes(lockedPath, EncryptLog("no url"));
        var fallbackPath = Path.Combine(installDir, "Saved", "Logs", "Client.log");
        Directory.CreateDirectory(Path.GetDirectoryName(fallbackPath)!);
        File.WriteAllText(fallbackPath, $"url={SampleUrl}");

        // FileShare.None 独占占位：模拟游戏进程正握着日志
        using var keeper = File.Open(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None);

        var info = CreateService().TryExtractGachaUrl(installDir);

        Assert.NotNull(info);
        Assert.Equal("100000002", info!.PlayerId);
    }

    [Fact]
    public void TryExtractGachaUrl_AllCandidatesLocked_ReturnsNullWithoutThrow()
    {
        var installDir = _tempDir.FilePath("WW");
        var paths = new[]
        {
            Path.Combine(installDir, "Client", "Saved", "Logs", "Client.log"),
            Path.Combine(installDir, "Saved", "Logs", "Client.log"),
        };
        var keepers = paths.Select(path =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, EncryptLog("no url"));
            return File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
        }).ToList();

        try
        {
            Assert.Null(CreateService().TryExtractGachaUrl(installDir));
        }
        finally
        {
            foreach (var keeper in keepers)
            {
                keeper.Dispose();
            }
        }
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("https://example.com/path")] // 绝对 URL 但无参数
    [InlineData("https://aki-gm-resources.aki-game.com/aki/gacha/index.html#/record?player_id=1&svr_id=2")] // 缺 record_id
    public void ParseGachaUrl_MalformedOrIncomplete_ReturnsNull(string url)
    {
        Assert.Null(KuroGachaService.ParseGachaUrl(url));
    }

    [Fact]
    public void ParseGachaUrl_HashParamsOverQuery_WhenQueryLacksRecordId()
    {
        // query 无 record_id 时回落 hash 段参数（两种官方落点都要认）
        var url = "https://aki-gm-resources.example.com/aki/gacha/index.html" +
                  "?x=1#/record?record_id=rec42&player_id=p1&svr_id=s1";

        var info = KuroGachaService.ParseGachaUrl(url);

        Assert.NotNull(info);
        Assert.Equal("rec42", info!.RecordId);
        Assert.False(info.IsChina); // 非 aki-game.com 域名
    }

    [Fact]
    public void LoadCached_CorruptFile_ReturnsEmpty()
    {
        // 审计缺口（2026-09-19）：缓存损坏（JsonException）按空列表兜底，不得让唤取页崩溃
        Directory.CreateDirectory(_tempDir.FilePath("gacha"));
        File.WriteAllText(_tempDir.FilePath("gacha", "wuthering-waves.json"), "{not-json");

        Assert.Empty(CreateService().LoadCached());
    }

    [Fact]
    public void MergeAndSave_UnwritableCacheDirectory_DoesNotThrow()
    {
        // 缓存目录是文件路径 → CreateDirectory 抛 IOException → 静默放弃（下次拉取重试）
        var filePath = _tempDir.FilePath("not-a-dir");
        File.WriteAllText(filePath, "occupied");

        var service = new KuroGachaService(new HttpClient(_handler), filePath);

        service.MergeAndSave([new GachaRecord("2026-09-01 10:00:00", "A", 5, 1)]);

        Assert.Empty(service.LoadCached());
    }

    [Fact]
    public void TryExtractGachaUrl_LogInWinePrefix_FoundViaPrefixCandidates()
    {
        // Linux + Proton 形态：安装目录没有任何日志，UE 日志落在 prefix 的
        // drive_c/users/<user>/AppData/Local/<项目>/Saved/Logs 下（不猜用户名与项目目录名）
        var installDir = _tempDir.FilePath("WW");
        Directory.CreateDirectory(installDir);
        var prefix = _tempDir.FilePath("prefix");
        var logPath = Path.Combine(
            prefix, "pfx", "drive_c", "users", "steamuser", "AppData", "Local",
            "WutheringWaves", "Client", "Saved", "Logs", "Client.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.WriteAllBytes(logPath, EncryptLog($"line {SampleUrl}"));

        var info = CreateService().TryExtractGachaUrl(installDir, winePrefixDirectory: prefix);

        Assert.NotNull(info);
        Assert.Equal("100000002", info!.PlayerId);
    }

    [Fact]
    public void TryExtractGachaUrl_InstallDirLogPreferredOverPrefix()
    {
        // 两处都有日志时按候选顺序取安装目录的（其内容最新鲜的概率最大）
        var installDir = _tempDir.FilePath("WW");
        Directory.CreateDirectory(Path.Combine(installDir, "Saved", "Logs"));
        File.WriteAllText(
            Path.Combine(installDir, "Saved", "Logs", "Client.log"),
            $"url={SampleUrl}");
        var prefix = _tempDir.FilePath("prefix");
        var logPath = Path.Combine(
            prefix, "pfx", "drive_c", "users", "steamuser", "AppData", "Local", "Proj", "Saved", "Logs", "Client.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.WriteAllText(logPath, "no url here");

        var info = CreateService().TryExtractGachaUrl(installDir, winePrefixDirectory: prefix);

        Assert.NotNull(info);
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
    public async Task FetchPoolAsync_MalformedQualityLevel_SkipsValueInsteadOfFailingWholeFetch()
    {
        // F41（artifacts/bugs.md）：qualityLevel 为对象/数组时 GetString() 抛 InvalidOperationException——
        // 单条畸形炸整个七池拉取（GachaViewModel foreach 全作废、已拉取全丢）。
        // 防线：数字/字符串之外按 0 缺省，同页其余记录保留（与不可解析缺省同语义）
        _handler.Map("https://gmserver-api.aki-game2.com/gacha/record/query", """
            {"code":0,"data":[
              {"time":"2026-09-01 10:00:00","name":"Good","qualityLevel":{"v":5}},
              {"time":"2026-09-01 09:00:00","name":"AlsoGood","qualityLevel":[4]}
            ]}
            """);
        var info = new GachaUrlInfo("76xx", "100000002", "zh-Hans", "rec123", IsChina: true);

        var records = await CreateService().FetchPoolAsync(info, 1);

        Assert.Equal(2, records.Count);
        Assert.All(records, r => Assert.Equal(0, r.QualityLevel));
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
