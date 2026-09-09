using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using YetAnotherGameLauncher.Core;

namespace YetAnotherGameLauncher.Channels.Kuro;

/// <summary>解析出的唤取记录页地址参数（URL 约 1 小时有效，凭证为 record_id）。</summary>
/// <param name="ServerId">服务器 id（svr_id）。</param>
/// <param name="PlayerId">玩家 id（player_id）。</param>
/// <param name="LanguageCode">语言（lang，如 zh-Hans）。</param>
/// <param name="RecordId">签名凭证（record_id）。</param>
/// <param name="IsChina">是否国服域名。</param>
public sealed record GachaUrlInfo(string ServerId, string PlayerId, string LanguageCode, string RecordId, bool IsChina);

/// <summary>一条唤取记录。</summary>
/// <param name="Time">记录时间（官方原始字符串）。</param>
/// <param name="Name">物品名。</param>
/// <param name="QualityLevel">稀有度（3/4/5）。</param>
/// <param name="PoolType">卡池类型（1-7，见 <see cref="GachaPools"/>）。</param>
public sealed record GachaRecord(string Time, string Name, int QualityLevel, int PoolType);

/// <summary>卡池类型枚举（官方 gacha_type 1-7）。</summary>
public static class GachaPools
{
    /// <summary>全部池类型值（角色精准/武器精准/角色常驻/武器常驻/新手/新手自选/感恩回馈）。</summary>
    public static readonly IReadOnlyList<int> All = [1, 2, 3, 4, 5, 6, 7];
}

/// <summary>
/// 鸣潮唤取（抽卡）记录服务：从游戏日志提取唤取记录页地址（可能 XOR 加密），再用该地址携带的
/// 签名参数查询官方 gacha/record/query 接口（无鉴权、URL 约 1 小时有效），结果合并进本地缓存
/// （URL 失效后历史仍可查）。实现参考社区共识（wutheringwaves-cli-manager / ww-script / WuWa_local_tracker）。
/// </summary>
public sealed partial class KuroGachaService(HttpClient httpClient, string? cacheDirectory = null)
{
    /// <summary>缓存文件名（按游戏）。</summary>
    private static string CacheFileName => "wuthering-waves.json";

    /// <summary>缓存目录（默认应用数据目录下 gacha/；测试注入临时目录）。</summary>
    private string CacheDirectory => cacheDirectory ?? Path.Combine(AppPaths.ConfigDirectory, "gacha");

    /// <summary>唤取记录页地址特征（出现在 Client.log 的行内）。</summary>
    [GeneratedRegex(@"https?://aki-gm-resources\.[^\s""`'/]+\.com/aki/gacha/index\.html[^\s""`']*")]
    private static partial Regex GachaUrlRegex();

    /// <summary>本地日志候选路径（相对游戏安装目录，官方两种落盘位置）。</summary>
    private static readonly string[] LogCandidates =
    [
        Path.Combine("Client", "Saved", "Logs", "Client.log"),
        Path.Combine("Saved", "Logs", "Client.log"),
    ];

    /// <summary>从游戏日志提取唤取记录地址；找不到（未打开过唤取记录/日志被清）返回 null。</summary>
    /// <param name="installDir">游戏安装目录。</param>
    public GachaUrlInfo? TryExtractGachaUrl(string? installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir))
        {
            return null;
        }

        foreach (var relative in LogCandidates)
        {
            var path = Path.Combine(installDir, relative);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var text = ReadLogText(path);
                var match = GachaUrlRegex().Match(text);
                if (!match.Success)
                {
                    continue;
                }

                return ParseGachaUrl(match.Value);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 日志被游戏占用（正在运行）：跳过下一个候选
            }
        }

        return null;
    }

    /// <summary>读取日志文本：magic 前缀 (\xA5\xEF\xA5) 视为 XOR 加密并解密，否则按 UTF-8 原样读。</summary>
    private static string ReadLogText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 3 && bytes[0] == 0xA5 && bytes[1] == 0xEF && bytes[2] == 0xA5)
        {
            var decoded = new byte[bytes.Length - 3];
            for (var i = 0; i < decoded.Length; i++)
            {
                // 官方混淆：解密循环里奇数索引异或 0xA5、偶数索引异或 0xEF
                decoded[i] = (byte)(bytes[i + 3] ^ (i % 2 == 1 ? 0xA5 : 0xEF));
            }

            return Encoding.UTF8.GetString(decoded);
        }

        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>解析唤取记录页 URL 的查询参数；缺关键参数（record_id/player_id/svr_id）返回 null。</summary>
    public static GachaUrlInfo? ParseGachaUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        // index.html 后是 '#/record?...' 形式的 hash 路由参数
        var rawQuery = uri.Query.StartsWith('?') ? uri.Query[1..] : uri.Query;
        var fragment = uri.Fragment.Length > 0 ? uri.Fragment[1..] : uri.Fragment;
        var hashQuery = fragment.Contains('?') ? fragment[(fragment.IndexOf('?') + 1)..] : "";
        var query = rawQuery.Length > 0 && rawQuery.Contains("record_id") ? rawQuery : hashQuery;

        string? Get(string name)
        {
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                if (kv.Length == 2 && kv[0] == name)
                {
                    return Uri.UnescapeDataString(kv[1]);
                }
            }

            return null;
        }

        var recordId = Get("record_id");
        var playerId = Get("player_id");
        var serverId = Get("svr_id");
        if (string.IsNullOrWhiteSpace(recordId) || string.IsNullOrWhiteSpace(playerId)
            || string.IsNullOrWhiteSpace(serverId))
        {
            return null;
        }

        return new GachaUrlInfo(
            serverId,
            playerId,
            Get("lang") is { Length: > 0 } lang ? lang : "zh-Hans",
            recordId,
            uri.Host.Contains("aki-game.com", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>拉取一个卡池的全部记录（官方按游标翻页，直到返回空集）。</summary>
    /// <param name="info">唤取地址参数。</param>
    /// <param name="poolType">卡池类型（1-7）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<IReadOnlyList<GachaRecord>> FetchPoolAsync(
        GachaUrlInfo info, int poolType, CancellationToken cancellationToken = default)
    {
        var apiBase = info.IsChina ? "https://gmserver-api.aki-game2.com" : "https://gmserver-api.aki-game2.net";
        var records = new List<GachaRecord>();
        string? cursor = null;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var request = new Dictionary<string, object?>
            {
                ["cardPoolId"] = info.RecordId,
                ["cardPoolType"] = poolType.ToString(CultureInfo.InvariantCulture),
                ["languageCode"] = info.LanguageCode,
                ["playerId"] = info.PlayerId,
                ["recordId"] = info.RecordId,
                ["serverId"] = info.ServerId,
            };
            if (!string.IsNullOrEmpty(cursor))
            {
                request["recordIdCursor"] = cursor;
            }

            using var response = await httpClient.PostAsJsonAsync(
                $"{apiBase}/gacha/record/query", request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            // CA2007 误报：await using 声明的 DisposeAsync 续体由编译器生成，无法对其追加 ConfigureAwait。
#pragma warning disable CA2007
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
#pragma warning restore CA2007

            var page = ParsePage(doc.RootElement, poolType);
            if (page.Count == 0)
            {
                break;
            }

            records.AddRange(page);

            // 官方未公开文档：游标取上一页最后一条的记录 id；缺字段/重复游标则停止防死循环
            var last = page[^1].Time + "|" + page[^1].Name;
            if (!seenCursors.Add(last))
            {
                break;
            }

            cursor = last;
            if (page.Count < 6)
            {
                break; // 不足一页：已是最后一页
            }
        }

        return records;
    }

    /// <summary>解析查询响应：code==0 且 data 数组（条目字段宽松解析，qualityLevel 兼容数字/字符串）。</summary>
    private static List<GachaRecord> ParsePage(JsonElement root, int poolType)
    {
        var result = new List<GachaRecord>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in data.EnumerateArray())
        {
            var time = item.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            if (time is null || name is null)
            {
                continue;
            }

            var quality = 0;
            if (item.TryGetProperty("qualityLevel", out var q))
            {
                quality = q.ValueKind == JsonValueKind.Number
                    ? (q.TryGetInt32(out var number) ? number : 0)
                    : int.TryParse(q.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                        ? parsed
                        : 0;
            }
            result.Add(new GachaRecord(time, name, quality, poolType));
        }

        return result;
    }

    /// <summary>读取本地合并缓存；文件缺失/损坏返回空列表。</summary>
    public IReadOnlyList<GachaRecord> LoadCached()
    {
        try
        {
            var path = Path.Combine(CacheDirectory, CacheFileName);
            if (!File.Exists(path))
            {
                return [];
            }

            var wrapper = JsonSerializer.Deserialize<GachaCache>(
                File.ReadAllText(path), GachaJsonOptions);
            return wrapper?.Records ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>把新拉取的记录与本地缓存按（时间+名字+稀有度+池）去重合并后写回。</summary>
    public void MergeAndSave(IReadOnlyList<GachaRecord> fetched)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            var merged = new Dictionary<string, GachaRecord>(StringComparer.Ordinal);
            foreach (var record in LoadCached())
            {
                merged[Key(record)] = record;
            }

            foreach (var record in fetched)
            {
                merged[Key(record)] = record;
            }

            var sorted = merged.Values.OrderByDescending(r => r.Time).ToList();
            File.WriteAllText(
                Path.Combine(CacheDirectory, CacheFileName),
                JsonSerializer.Serialize(new GachaCache(sorted), GachaJsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 缓存写失败不致命：下次拉取重试
        }
    }

    private static string Key(GachaRecord record) =>
        $"{record.Time}|{record.Name}|{record.QualityLevel}|{record.PoolType}";

    private static readonly JsonSerializerOptions GachaJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>缓存文件结构。</summary>
    /// <param name="Records">全部记录（按时间倒序）。</param>
    private sealed record GachaCache(IReadOnlyList<GachaRecord> Records);
}
