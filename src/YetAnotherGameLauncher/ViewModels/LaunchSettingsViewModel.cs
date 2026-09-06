using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Services;

namespace YetAnotherGameLauncher.ViewModels;

/// <summary>
/// 详情页"启动设置"卡：编辑命令模板 / 工作目录 / 环境变量并保存回 games.json。
/// 环境变量以多行 KEY=VALUE 文本编辑（解析容错，错误行给出行内容提示）。
/// </summary>
public partial class LaunchSettingsViewModel : ViewModelBase
{
    private readonly GameDefinition _game;
    private readonly GameItemViewModel _owner;
    private readonly GameCatalogService _catalogService;
    private readonly ILocalizationService _loc;

    public LaunchSettingsViewModel(
        GameDefinition game,
        string installDir,
        GameCatalogService catalogService,
        ILocalizationService loc,
        GameItemViewModel owner)
    {
        _game = game;
        _owner = owner;
        _catalogService = catalogService;
        _loc = loc;
        _installDirDraft = installDir;
        _commandTemplate = game.Launch.CommandTemplate;
        _workingDirectory = game.Launch.WorkingDirectory;
        _environmentText = SerializeEnvironment(game.Launch.Environment);
    }

    /// <summary>安装目录草稿（绝对路径；与启动参数共用同一保存按钮）。</summary>
    [ObservableProperty]
    private string _installDirDraft;

    public ILocalizationService Loc => _loc;

    [ObservableProperty]
    private string _commandTemplate;

    [ObservableProperty]
    private string _workingDirectory;

    [ObservableProperty]
    private string _environmentText;

    /// <summary>保存结果提示（成功或错误原因）；空 = 无提示。</summary>
    [ObservableProperty]
    private string _saveMessage = "";

    [ObservableProperty]
    private bool _saveFailed;

    [RelayCommand]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        SaveMessage = "";
        SaveFailed = false;

        if (string.IsNullOrWhiteSpace(CommandTemplate))
        {
            SaveFailed = true;
            SaveMessage = _loc["launch_commandTemplateRequired"];
            return;
        }

        var installDir = InstallDirDraft.Trim();
        if (installDir.Length == 0)
        {
            SaveFailed = true;
            SaveMessage = _loc["launch_installDirRequired"];
            return;
        }

        if (!TryParseEnvironment(EnvironmentText, out var environment, out var badLine))
        {
            SaveFailed = true;
            SaveMessage = _loc.Format("launch_invalidEnvLine", badLine);
            return;
        }

        var installDirChanged = !string.Equals(installDir, _owner.InstallDirPath, StringComparison.Ordinal);
        if (installDirChanged)
        {
            _game.InstallDir = installDir; // 支持绝对路径，直接写回
        }

        _game.Launch = new LaunchOptions
        {
            CommandTemplate = CommandTemplate.Trim(),
            WorkingDirectory = string.IsNullOrWhiteSpace(WorkingDirectory)
                ? "{installDir}"
                : WorkingDirectory.Trim(),
            Environment = environment,
        };

        try
        {
            await _catalogService.SaveAsync(cancellationToken);
            if (installDirChanged)
            {
                _owner.UpdateInstallDir(installDir); // 路径就地重解析 + 状态刷新
            }

            SaveMessage = _loc["launch_saved"];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveFailed = true;
            SaveMessage = _loc.Format("message_saveFailed", ex.Message);
        }
    }

    internal static string SerializeEnvironment(Dictionary<string, string> environment)
        => string.Join(Environment.NewLine, environment.Select(kv => $"{kv.Key}={kv.Value}"));

    private static bool TryParseEnvironment(
        string text, out Dictionary<string, string> environment, out string badLine)
    {
        environment = [];
        badLine = "";
        foreach (var raw in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var sep = line.IndexOf('=');
            if (sep <= 0)
            {
                badLine = line;
                return false;
            }

            environment[line[..sep].Trim()] = line[(sep + 1)..].Trim();
        }

        return true;
    }
}
