using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YetAnotherGameLauncher.Channels.Kuro;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Services;

namespace YetAnotherGameLauncher.ViewModels;

/// <summary>鸣潮唤取（抽卡）记录页：从游戏日志提取地址 → 官方接口拉取 → 本地合并缓存与展示。</summary>
public partial class GachaViewModel : ViewModelBase
{
    /// <summary>官方每页条数下限：返回条数少于此值视为最后一页（用于游标翻页停止判断）。</summary>
    private const int FullPageSize = 6;

    private readonly MainWindowViewModel _owner;
    private readonly KuroGachaService _gachaService;
    private readonly GameItemViewModel _game;

    public GachaViewModel(MainWindowViewModel owner, GameItemViewModel game, KuroGachaService gachaService)
    {
        _owner = owner;
        _game = game;
        _gachaService = gachaService;
        _displayName = game.DisplayName;
        Pools =
        [
            new GachaPoolOption(0, Loc["gacha_pool_all"]),
            .. GachaPools.All.Select(p => new GachaPoolOption(p, Loc[$"gacha_pool_{p}"])),
        ];
        _selectedPool = Pools[0];
    }

    /// <summary>文案服务（转发主窗口实例）。</summary>
    public ILocalizationService Loc => _owner.Loc;

    /// <summary>所属游戏显示名。</summary>
    [ObservableProperty]
    private string _displayName;

    /// <summary>返回游戏详情（转发主窗口导航，保持游戏侧栏高亮）。</summary>
    [RelayCommand]
    private void ShowGame() => _owner.ShowGamesCommand.Execute(null);

    /// <summary>卡池筛选选项（全部 + 1-7）。</summary>
    public IReadOnlyList<GachaPoolOption> Pools { get; }

    /// <summary>当前筛选的卡池。</summary>
    [ObservableProperty]
    private GachaPoolOption _selectedPool;

    partial void OnSelectedPoolChanged(GachaPoolOption value) => RebuildView();

    /// <summary>筛选后的记录（时间倒序；五星/四星由 UI 按稀有度着色）。</summary>
    public ObservableCollection<GachaRecordView> Records { get; } = [];

    /// <summary>状态提示（拉取结果/无地址引导/失败原因）。</summary>
    [ObservableProperty]
    private string _statusText = "";

    /// <summary>状态是否为引导性提示（非错误）。</summary>
    [ObservableProperty]
    private bool _isStatusHint = true;

    /// <summary>拉取是否进行中。</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>累计抽数（当前筛选池）。</summary>
    [ObservableProperty]
    private int _totalCount;

    /// <summary>五星数量（当前筛选池）。</summary>
    [ObservableProperty]
    private int _fiveStarCount;

    /// <summary>四星数量（当前筛选池）。</summary>
    [ObservableProperty]
    private int _fourStarCount;

    /// <summary>当前池距最近一个五星的抽数（保底进度，全部池视图无意义则显示总计）。</summary>
    [ObservableProperty]
    private int _sinceLastFiveStar;

    /// <summary>页面打开即自动尝试拉取（地址有效时一步到位）。</summary>
    [RelayCommand]
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        IsStatusHint = true;
        StatusText = Loc["gacha_status_loading"];
        try
        {
            var info = _gachaService.TryExtractGachaUrl(_game.InstallDirPath);
            if (info is null)
            {
                StatusText = Loc["gacha_status_noUrl"];
                LoadFromCache();
                return;
            }

            var fetched = new List<GachaRecord>();
            foreach (var pool in GachaPools.All)
            {
                fetched.AddRange(await _gachaService.FetchPoolAsync(info, pool, cancellationToken));
            }

            _gachaService.MergeAndSave(fetched);
            LoadFromCache();
            StatusText = Loc.Format("gacha_status_fetched", fetched.Count);
        }
        catch (Exception ex) when ((ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
            && !cancellationToken.IsCancellationRequested)
        {
            IsStatusHint = false;
            StatusText = Loc["gacha_status_failed"];
            LoadFromCache();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>从本地缓存载入并按当前筛选重建视图与统计。</summary>
    private void LoadFromCache()
    {
        _allRecords.Clear();
        foreach (var record in _gachaService.LoadCached())
        {
            _allRecords.Add(record);
        }

        RebuildView();
    }

    private readonly List<GachaRecord> _allRecords = [];

    /// <summary>按筛选池重算记录列表与统计（五星保底按池独立累计）。</summary>
    private void RebuildView()
    {
        var pool = SelectedPool?.Type ?? 0;
        var view = pool == 0 ? _allRecords : _allRecords.Where(r => r.PoolType == pool);
        var ordered = view.OrderByDescending(r => r.Time).ToList();

        Records.Clear();
        foreach (var record in ordered)
        {
            Records.Add(new GachaRecordView(record));
        }

        TotalCount = ordered.Count;
        FiveStarCount = ordered.Count(r => r.QualityLevel >= 5);
        FourStarCount = ordered.Count(r => r.QualityLevel == 4);
        SinceLastFiveStar = 0;
        foreach (var record in ordered) // 时间倒序：遇到首个五星即为止
        {
            if (record.QualityLevel >= 5)
            {
                break;
            }

            SinceLastFiveStar++;
        }
    }

}

/// <summary>记录列表的展示项（原始记录 + 预计算的稀有度着色，供 XAML 绑定）。</summary>
/// <param name="Record">原始记录。</param>
public sealed record GachaRecordView(GachaRecord Record)
{
    /// <summary>记录时间。</summary>
    public string Time => Record.Time;

    /// <summary>物品名。</summary>
    public string Name => Record.Name;

    /// <summary>稀有度数字。</summary>
    public int QualityLevel => Record.QualityLevel;

    /// <summary>是否五星（金色标记）。</summary>
    public bool Rare5 => Record.QualityLevel >= 5;

    /// <summary>是否四星（紫色标记）。</summary>
    public bool Rare4 => Record.QualityLevel == 4;
}

/// <summary>卡池筛选下拉项。</summary>
/// <param name="Type">池类型值（0 = 全部）。</param>
/// <param name="Name">显示名（本地化）。</param>
public sealed record GachaPoolOption(int Type, string Name);
