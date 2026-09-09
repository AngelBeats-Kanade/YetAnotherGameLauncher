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
    private readonly IFilePickerService? _filePicker;

    public LaunchSettingsViewModel(
        GameDefinition game,
        string installDir,
        GameCatalogService catalogService,
        ILocalizationService loc,
        GameItemViewModel owner,
        IFilePickerService? filePicker = null)
    {
        _game = game;
        _owner = owner;
        _catalogService = catalogService;
        _loc = loc;
        _filePicker = filePicker;
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

        // Linux 默认最佳配置：裸 {exe} 无法运行 Windows 客户端 → 应用社区推荐（进草稿，保存后才落盘；
        // 用户已有任何自定义模板则完全不动）
        if (IsLinux && _selectedLaunchMode.Mode == LaunchMode.Direct)
        {
            var recommended = CompatTools.PickRecommendedProton(ProtonVersions);
            if (recommended is not null)
            {
                SelectedLaunchMode = LaunchModes.First(m => m.Mode == LaunchMode.Proton);
                _selectedProtonVersion = recommended;
                ApplyGenerated(CompatTools.BuildProtonLaunch(recommended));
                AppendEnvironment(CompatTools.RecommendedEnvironment(_game.Id));
            }
            else
            {
                SelectedLaunchMode = LaunchModes.First(m => m.Mode == LaunchMode.Wine);
            }
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

    /// <summary>Proton 版本变化时触发：用所选版本重新生成命令模板与兼容环境。</summary>
    partial void OnSelectedProtonVersionChanged(string? value)
    {
        if (SelectedLaunchMode?.Mode == LaunchMode.Proton && !string.IsNullOrWhiteSpace(value))
        {
            ApplyGenerated(CompatTools.BuildProtonLaunch(value));
        }
    }

    /// <summary>启动方式变化时触发：刷新 Proton 可见性，按方式生成命令模板并增删兼容环境变量。</summary>
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

    /// <summary>应用生成的启动配置：覆盖命令模板，生成的环境变量按 KEY 合并进现有文本。</summary>
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

    /// <summary>把推荐环境变量合并进环境文本（既有同名值以用户为准）。</summary>
    private void AppendEnvironment(Dictionary<string, string> extra)
    {
        var merged = ParseEnvironmentOrEmpty(EnvironmentText);
        foreach (var (key, value) in extra)
        {
            if (!merged.ContainsKey(key))
            {
                merged[key] = value;
            }
        }

        EnvironmentText = SerializeEnvironment(merged);
    }

    /// <summary>从环境文本中移除全部 STEAM_COMPAT_* 变量（切回直接/Wine 启动时调用）。</summary>
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

    /// <summary>宽松解析环境文本为字典（跳过无 "=" 的行，不报错）；行级解析复用严格版，保证切分规则单一。</summary>
    private static Dictionary<string, string> ParseEnvironmentOrEmpty(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryParseEnvironment(raw, out var single, out _))
            {
                foreach (var (key, value) in single)
                {
                    result[key] = value;
                }
            }
        }

        return result;
    }

    /// <summary>启动命令模板草稿（{exe} 为游戏可执行文件占位符）。</summary>
    [ObservableProperty]
    private string _commandTemplate;

    /// <summary>工作目录草稿（空白 = 保存时回退 {installDir}）。</summary>
    [ObservableProperty]
    private string _workingDirectory;

    /// <summary>环境变量草稿（多行 KEY=VALUE 文本）。</summary>
    [ObservableProperty]
    private string _environmentText;

    /// <summary>保存结果提示（显示在游戏设置页位置卡内）。</summary>
    [ObservableProperty]
    private SaveMessageSlot _save = new();

    /// <summary>
    /// 弹系统目录选择对话框选游戏安装目录：选中即写入草稿并保存（替代原"保存"按钮），取消则不动草稿。
    /// 未注册选择器（无头测试/服务缺失）时命令无副作用。
    /// </summary>
    [RelayCommand]
    private async Task BrowseInstallDirAsync(CancellationToken cancellationToken)
    {
        Save.Clear();
        if (_filePicker is null)
        {
            return;
        }

        var path = await _filePicker.PickFolderAsync(_loc["launch_installDirPickTitle"], InstallDirDraft);
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        InstallDirDraft = NormalizeDirectoryPath(path);
        await SaveAsync(cancellationToken);
    }

    /// <summary>目录路径统一为正斜杠（与示例配置一致；读取端 Path.GetFullPath 兼容两种斜杠）。</summary>
    private static string NormalizeDirectoryPath(string path) => path.Replace('\\', '/');

    /// <summary>校验并保存启动设置回 games.json（含安装目录变更时就地生效）。</summary>
    [RelayCommand]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        Save.Clear();

        if (string.IsNullOrWhiteSpace(CommandTemplate))
        {
            Save.SetFailure(_loc["launch_commandTemplateRequired"]);
            return;
        }

        var installDir = InstallDirDraft.Trim();
        if (installDir.Length == 0)
        {
            Save.SetFailure(_loc["launch_installDirRequired"]);
            return;
        }

        if (!TryParseEnvironment(EnvironmentText, out var environment, out var badLine))
        {
            Save.SetFailure(_loc.Format("launch_invalidEnvLine", badLine));
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

            Save.SetSuccess(_loc["launch_saved"]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Save.SetFailure(_loc.Format("message_saveFailed", ex.Message));
        }
    }

    /// <summary>环境变量字典 → 多行 KEY=VALUE 文本（编辑框显示用）。</summary>
    private static string SerializeEnvironment(Dictionary<string, string> environment)
        => string.Join(Environment.NewLine, environment.Select(kv => $"{kv.Key}={kv.Value}"));

    /// <summary>多行 KEY=VALUE 文本 → 字典；出错的行写入 badLine 返回 false（解析容错与保存校验共用）。</summary>
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

/// <summary>启动方式选项（模式 + 已本地化文案）。</summary>
public sealed record LaunchModeOption(LaunchMode Mode, string Name);
