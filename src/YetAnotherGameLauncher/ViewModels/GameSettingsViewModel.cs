using CommunityToolkit.Mvvm.Input;
using YetAnotherGameLauncher.Services;

namespace YetAnotherGameLauncher.ViewModels;

/// <summary>
/// 游戏设置次页（从详情页齿轮进入，Motrix 式分区）：
/// 游戏位置 / 启动方式与兼容层 / 启动参数 / 游戏信息。
/// 编辑草稿与保存逻辑复用 LaunchSettingsViewModel。
/// </summary>
public partial class GameSettingsViewModel(GameItemViewModel game, MainWindowViewModel owner) : ViewModelBase
{
    /// <summary>正在设置的游戏。</summary>
    public GameItemViewModel Game { get; } = game;

    /// <summary>暴露给 XAML 的文案服务（页面标题等绑定 {Binding Loc[key]}）。</summary>
    public ILocalizationService Loc => owner.Loc;

    /// <summary>位置/启动参数编辑卡（与详情页共享同一实例）。</summary>
    public LaunchSettingsViewModel LaunchSettings => Game.LaunchSettings;

    [RelayCommand]
    private void BackToGame() => owner.ShowGamesCommand.Execute(null);
}
