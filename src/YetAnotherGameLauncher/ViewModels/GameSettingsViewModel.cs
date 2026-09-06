using CommunityToolkit.Mvvm.Input;
using YetAnotherGameLauncher.Services;

namespace YetAnotherGameLauncher.ViewModels;

/// <summary>
/// 游戏设置次页（从详情页齿轮进入，Motrix 式分区）：
/// 游戏位置 / 启动方式与兼容层 / 启动参数 / 游戏信息。
/// 编辑草稿与保存逻辑复用 LaunchSettingsViewModel。
/// </summary>
public partial class GameSettingsViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _owner;

    public GameSettingsViewModel(GameItemViewModel game, MainWindowViewModel owner)
    {
        Game = game;
        _owner = owner;
    }

    public GameItemViewModel Game { get; }

    public ILocalizationService Loc => _owner.Loc;

    public LaunchSettingsViewModel LaunchSettings => Game.LaunchSettings;

    [RelayCommand]
    private void BackToGame() => _owner.ShowGamesCommand.Execute(null);
}
