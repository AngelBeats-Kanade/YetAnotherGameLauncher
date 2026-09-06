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
        _selectedLaunchMode = DetectLaunchMode(game.Launch.CommandTemplate);
        if (IsLinux && _selectedLaunchMode.Mode == LaunchMode.Proton && ProtonVersions.Count > 0)
        {
            _selectedProtonVersion = ProtonVersions.Contains(
                CompatTools.DefaultProton, StringComparer.OrdinalIgnoreCase)
                ? CompatTools.DefaultProton
                : ProtonVersions[0];
        }
    }

    /// <summary>从命令模板推断当前启动方式（启发式：含 proton/wine 关键词；{exe} 原样视为直接运行）。</summary>
    private static LaunchModeOption DetectLaunchMode(string commandTemplate)
    {
        var t = commandTemplate.Trim();
        if (t.Contains("proton", StringComparison.OrdinalIgnoreCase))
        {
            return new LaunchModeOption(LaunchMode.Proton, "launch_mode_proton");
        }

        if (t.Contains("wine", StringComparison.OrdinalIgnoreCase))
        {
            return new LaunchModeOption(LaunchMode.Wine, "launch_mode_wine");
        }

        return t == "{exe}"
            ? new LaunchModeOption(LaunchMode.Direct, "launch_mode_direct")
            : new LaunchModeOption(LaunchMode.Custom, "launch_mode_custom");
    }

    /// <summary>安装目录草稿（绝对路径；与启动参数共用同一保存按钮）。</summary>
    [ObservableProperty]
    private string _installDirDraft;

    /// <summary>当前系统是否为 Linux（决定是否显示兼容层选择）。</summary>
    public bool IsLinux => OperatingSystem.IsLinux();

    public IReadOnlyList<LaunchModeOption> LaunchModes { get; } =
    [
        new(LaunchMode.Direct, LocBridge.Instance["launch_mode_direct"]),
        new(LaunchMode.Wine, LocBridge.Instance["launch_mode_wine"]),
        new(LaunchMode.Proton, LocBridge.Instance["launch_mode_proton"]),
        new(LaunchMode.Custom, LocBridge.Instance["launch_mode_custom"]),
    ];

    [ObservableProperty]
    private LaunchModeOption? _selectedLaunchMode;

    /// <summary>是否处于 Proton 启动方式（决定版本选择器可见性）。</summary>
    public bool IsProtonMode => SelectedLaunchMode?.Mode == LaunchMode.Proton;

    public IReadOnlyList<string> ProtonVersions { get; } =
        OperatingSystem.IsLinux() ? CompatTools.FindProtonVersions() : [];

    [ObservableProperty]
    private string? _selectedProtonVersion;

    partial void OnSelectedProtonVersionChanged(string? value)
    {
        if (SelectedLaunchMode?.Mode == LaunchMode.Proton && !string.IsNullOrWhiteSpace(value))
        {
            ApplyGenerated(CompatTools.BuildProtonLaunch(value));
        }
    }

    partial void OnSelectedLaunchModeChanged(LaunchModeOption? value)
    {
        OnPropertyChanged(nameof(IsProtonMode));
        if (value is null)
        {
            return;
        }

        switch (value.Mode)
        {
            case LaunchMode.Direct:
                CommandTemplate = "{exe}";
                RemoveCompatEnvironment();
                break;
            case LaunchMode.Wine:
                CommandTemplate = "wine {exe}";
                RemoveCompatEnvironment();
                break;
            case LaunchMode.Proton:
                if (string.IsNullOrWhiteSpace(SelectedProtonVersion) && ProtonVersions.Count > 0)
                {
                    SelectedProtonVersion = ProtonVersions[0]; // 触发生成
                }
                else if (!string.IsNullOrWhiteSpace(SelectedProtonVersion))
                {
                    ApplyGenerated(CompatTools.BuildProtonLaunch(SelectedProtonVersion));
                }
                break;
            case LaunchMode.Custom:
            default:
                break; // 自定义：不动草稿
        }
    }

    private void ApplyGenerated((string CommandTemplate, Dictionary<string, string> Environment) generated)
    {
        CommandTemplate = generated.CommandTemplate;
        var merged = ParseEnvironmentOrEmpty(EnvironmentText);
        foreach (var (key, value) in generated.Environment)
        {
            merged[key] = value;
        }

        EnvironmentText = SerializeEnvironment(merged);
    }

    private void RemoveCompatEnvironment()
    {
        var merged = ParseEnvironmentOrEmpty(EnvironmentText);
        var keys = merged.Keys.Where(k =>
                k.StartsWith("STEAM_COMPAT_", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var key in keys)
        {
            merged.Remove(key);
        }

        EnvironmentText = SerializeEnvironment(merged);
    }

    private static Dictionary<string, string> ParseEnvironmentOrEmpty(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            var sep = line.IndexOf('=');
            if (sep > 0)
            {
                result[line[..sep].Trim()] = line[(sep + 1)..].Trim();
            }
        }

        return result;
    }

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

    private static string SerializeEnvironment(Dictionary<string, string> environment)
        => string.Join(Environment.NewLine, environment.Select(kv => $"{kv.Key}={kv.Value}"));

    private static bool TryParseEnvironment(
        string text, out Dictionary<string, string> environment, out string badLine)
    {
        environment = [];
        badLine = "";
        foreach (var raw in text.Split([(char)13, (char)10], StringSplitOptions.RemoveEmptyEntries))
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

/// <summary>启动方式选项（模式 + 已本地化文案）。</summary>
public sealed record LaunchModeOption(LaunchMode Mode, string Name);
