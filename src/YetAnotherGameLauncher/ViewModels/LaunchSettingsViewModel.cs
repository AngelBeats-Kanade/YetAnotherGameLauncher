using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YetAnotherGameLauncher.Core;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Services;

namespace YetAnotherGameLauncher.ViewModels;

/// <summary>
/// 详情页"启动设置"卡：编辑命令模板 / 工作目录 / 环境变量并保存回 games.json。
/// 环境变量以多行 KEY=VALUE 文本编辑（解析容错，错误行给出行内容提示）。
/// 原生 umu 模式显示组件状态并可一键检查/下载 Proton 与 Steam Runtime。
/// </summary>
public partial class LaunchSettingsViewModel : ViewModelBase
{
    private readonly GameDefinition _game;
    private readonly GameItemViewModel _owner;
    private readonly GameCatalogService _catalogService;
    private readonly ILocalizationService _loc;
    private readonly IFilePickerService? _filePicker;
    private readonly Core.Abstractions.IPlatformInfo _platform;
    private readonly IReadOnlyList<string> _protonVersions;

    /// <summary>已发现的运行时路径与数据目录（null = 未发现/未注入；测试显式传值保证确定性）。</summary>
    private string? _umuRunPath;
    private readonly string? _winePath;
    private readonly string _dataHome;
    private readonly UmuLauncherInstaller? _umuInstaller;
    private readonly IUmuComponentProvisioner? _umuProvisioner;

    public LaunchSettingsViewModel(
        GameDefinition game,
        string installDir,
        GameCatalogService catalogService,
        ILocalizationService loc,
        GameItemViewModel owner,
        IFilePickerService? filePicker = null,
        Core.Abstractions.IPlatformInfo? platformInfo = null,
        IReadOnlyList<string>? protonVersions = null,
        string? umuRunPath = null,
        string? winePath = null,
        string? dataHome = null,
        UmuLauncherInstaller? umuInstaller = null,
        IUmuComponentProvisioner? umuProvisioner = null)
    {
        _game = game;
        _owner = owner;
        _catalogService = catalogService;
        _loc = loc;
        _filePicker = filePicker;
        _platform = platformInfo ?? (OperatingSystem.IsLinux()
            ? new Core.Services.LinuxPlatformInfo()
            : new Core.Services.WindowsPlatformInfo());
        _installDirDraft = installDir;
        _executableDraft = game.Executable;
        _protonVersions = protonVersions ?? (_platform.IsLinux ? CompatTools.FindProtonVersions() : []);
        // 显式空串 = 声明"没装"（测试禁用真机 PATH 扫描）；null = 现场（仅 Linux）发现
        _umuRunPath = umuRunPath is null
            ? (IsLinux ? CompatTools.FindUmuRun() : null)
            : (umuRunPath.Length == 0 ? null : umuRunPath);
        _winePath = winePath is null
            ? (IsLinux ? CompatTools.FindSystemWine() : null)
            : (winePath.Length == 0 ? null : winePath);
        _dataHome = dataHome ?? AppPaths.DataHomeDirectory; // CompatTools 语义要求数据根（不含 yagl 后缀）
        _umuInstaller = umuInstaller;
        _umuProvisioner = umuProvisioner;
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

        // Linux 默认最佳配置：裸 {exe} 无法运行 Windows 客户端 → 应用社区推荐链（进草稿，保存后才落盘；
        // 用户已有任何自定义模板则完全不动）
        if (IsLinux && _selectedLaunchMode.Mode == LaunchMode.Direct)
        {
            var launch = CompatTools.BuildRecommendedLaunch(
                _game.Id, ProtonVersions, _platform.IsNvidiaGpuPresent,
                dataHome: _dataHome, umuRunPath: _umuRunPath, winePath: _winePath);
            SelectedLaunchMode = LaunchModes.First(m => m.Mode == launch.Mode);
            if (launch.Mode == LaunchMode.Proton)
            {
                _selectedProtonVersion = launch.RuntimeName;
            }

            ApplyGenerated((launch.CommandTemplate, launch.Environment));
        }

        RefreshNativeUmuStatus();
    }

    /// <summary>从命令模板推断当前启动方式（启发式：含 proton/umu/wine 关键词；{exe} 原样视为直接运行）。
    /// 返回 LaunchModes 集合内的实例——ComboBox 的 SelectedItem 按引用匹配，游离实例会显示为空白。</summary>
    private LaunchModeOption DetectLaunchMode(string commandTemplate)
    {
        var t = commandTemplate.Trim();
        var mode = t.Contains("native-umu", StringComparison.OrdinalIgnoreCase) ? LaunchMode.NativeUmu
            : t.Contains("proton", StringComparison.OrdinalIgnoreCase) ? LaunchMode.Proton
            : t.Contains("umu-run", StringComparison.OrdinalIgnoreCase) ? LaunchMode.Umu
            : t.Contains("wine", StringComparison.OrdinalIgnoreCase) ? LaunchMode.Wine
            : t == "{exe}" ? LaunchMode.Direct
            : LaunchMode.Custom;
        return LaunchModes.First(m => m.Mode == mode);
    }

    /// <summary>安装目录草稿（绝对路径；与启动参数共用同一保存按钮）。</summary>
    [ObservableProperty]
    private string _installDirDraft;

    /// <summary>游戏可执行文件草稿（相对安装目录或绝对路径，'/' 分隔）。</summary>
    [ObservableProperty]
    private string _executableDraft;

    /// <summary>当前系统是否为 Linux（决定是否显示兼容层选择；平台信息注入，测试可控）。</summary>
    public bool IsLinux => _platform.IsLinux;

    public IReadOnlyList<LaunchModeOption> LaunchModes { get; } =
    [
        new(LaunchMode.Direct, LocBridge.Instance["launch_mode_direct"]),
        new(LaunchMode.NativeUmu, LocBridge.Instance["launch_mode_native_umu"]),
        new(LaunchMode.Umu, LocBridge.Instance["launch_mode_umu"]),
        new(LaunchMode.Wine, LocBridge.Instance["launch_mode_wine"]),
        new(LaunchMode.Proton, LocBridge.Instance["launch_mode_proton"]),
        new(LaunchMode.Custom, LocBridge.Instance["launch_mode_custom"]),
    ];

    [ObservableProperty]
    private LaunchModeOption? _selectedLaunchMode;

    /// <summary>是否处于 Proton 启动方式（决定版本选择器可见性）。</summary>
    public bool IsProtonMode => SelectedLaunchMode?.Mode == LaunchMode.Proton;

    /// <summary>是否处于原生 umu 启动方式（内置 C# 链，无需外部 umu-run）。</summary>
    public bool IsNativeUmuMode => SelectedLaunchMode?.Mode == LaunchMode.NativeUmu;

    /// <summary>原生 umu 组件准备是否进行中。</summary>
    [ObservableProperty]
    private bool _isPreparingUmuComponents;

    /// <summary>原生 umu 组件状态摘要（就绪 / 缺 Proton / 缺 Runtime / 未检查）。</summary>
    [ObservableProperty]
    private string _nativeUmuStatusText = "";

    /// <summary>是否可执行「检查/下载兼容组件」（Linux + 原生 umu + 准备器可用）。</summary>
    public bool CanPrepareUmuComponents =>
        IsNativeUmuMode && IsLinux && _umuProvisioner is not null && !IsPreparingUmuComponents;

    /// <summary>进入原生 umu 模式时刷新组件状态摘要（不触网）。
    /// 判定与启动时 Ensure* 请求对齐：只认 ResolveNativeProtonRequest 命中的 Proton / 默认 Runtime。</summary>
    private void RefreshNativeUmuStatus()
    {
        if (!IsNativeUmuMode || _umuProvisioner is null)
        {
            NativeUmuStatusText = "";
            OnPropertyChanged(nameof(CanPrepareUmuComponents));
            return;
        }

        var protonRequest = ResolveNativeProtonRequest();
        var protonReady = _umuProvisioner.IsProtonReady(protonRequest)
            || (!Path.IsPathRooted(protonRequest)
                && _protonVersions.Any(v =>
                    _umuProvisioner.IsProtonReady(Path.Combine(
                        Core.Services.Umu.UmuPaths.SteamCompatRoot(_dataHome), v))
                    && v.StartsWith(
                        protonRequest.StartsWith("UMU", StringComparison.OrdinalIgnoreCase) ? "UMU-Proton" : "GE-Proton",
                        StringComparison.OrdinalIgnoreCase)));
        var runtimeReady = _umuProvisioner.IsRuntimeReady(
            Core.Services.Umu.SteamRuntimeCatalog.Default.Variant);
        NativeUmuStatusText = protonReady && runtimeReady
            ? _loc["launch_native_components_ready"]
            : protonReady
                ? _loc["launch_native_runtime_missing"]
                : _loc["launch_native_components_missing"];
        OnPropertyChanged(nameof(CanPrepareUmuComponents));
    }

    /// <summary>解析原生 umu 用的 Proton 请求：环境 PROTONPATH → 本机推荐版本 → UMU-Proton 代号。</summary>
    private string ResolveNativeProtonRequest()
    {
        if (ParseEnvironmentOrEmpty(EnvironmentText).TryGetValue("PROTONPATH", out var path)
            && !string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        return CompatTools.PickRecommendedProton(_protonVersions) ?? "UMU-Proton";
    }

    /// <summary>检查/下载原生 umu 兼容组件（Proton + Steam Runtime）；结果写入保存消息槽。</summary>
    [RelayCommand]
    private async Task PrepareUmuComponentsAsync(CancellationToken cancellationToken)
    {
        if (_umuProvisioner is null || IsPreparingUmuComponents || !IsLinux)
        {
            return;
        }

        IsPreparingUmuComponents = true;
        OnPropertyChanged(nameof(CanPrepareUmuComponents));
        Save.Clear();
        try
        {
            var progress = new Progress<string>(msg =>
            {
                Save.Clear();
                Save.SetSuccess(msg);
            });
            var proton = await _umuProvisioner
                .EnsureProtonAsync(ResolveNativeProtonRequest(), progress, cancellationToken)
                .ConfigureAwait(true);
            var runtime = Core.Services.Umu.SteamRuntimeCatalog.Default;
            await _umuProvisioner
                .EnsureRuntimeAsync(runtime.Variant, runtime.Name, progress, cancellationToken)
                .ConfigureAwait(true);
            Save.Clear();
            Save.SetSuccess(_loc["launch_native_components_ready"]);
            RefreshNativeUmuStatus();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Save.SetFailure(ex.Message);
            RefreshNativeUmuStatus();
        }
        finally
        {
            IsPreparingUmuComponents = false;
            OnPropertyChanged(nameof(CanPrepareUmuComponents));
        }
    }

    /// <summary>是否处于 umu 启动方式（决定 umu 安装引导提示可见性）。</summary>
    public bool IsUmuMode => SelectedLaunchMode?.Mode == LaunchMode.Umu;

    /// <summary>umu-run 是否已发现（未发现时提示可一键引导安装）。</summary>
    public bool IsUmuAvailable => _umuRunPath is not null;

    /// <summary>是否显示 umu 未安装提示（选中 umu 模式且未装）。</summary>
    public bool IsUmuHintVisible => IsUmuMode && !IsUmuAvailable;

    /// <summary>umu 引导安装进行中。</summary>
    [ObservableProperty]
    private bool _isInstallingUmu;

    /// <summary>一键安装 umu-launcher；成功后刷新发现结果并按当前模式重新生成草稿。</summary>
    [RelayCommand]
    private async Task InstallUmuAsync(CancellationToken cancellationToken)
    {
        if (_umuInstaller is null || IsInstallingUmu)
        {
            return;
        }

        IsInstallingUmu = true;
        try
        {
            var installDir = Path.Combine(AppPaths.DataDirectory, "umu");
            var installed = await _umuInstaller
                .InstallLatestAsync(installDir, cancellationToken: cancellationToken).ConfigureAwait(true);
            _umuRunPath = installed; // 装完就地生效：提示消失，模板按 umu 重新生成
            Save.Clear();
            Save.SetSuccess(_loc["launch_error_umu_done"]);
            OnPropertyChanged(nameof(IsUmuAvailable));
            OnPropertyChanged(nameof(IsUmuHintVisible));
            if (SelectedLaunchMode?.Mode == LaunchMode.Umu)
            {
                ApplyGenerated(Flatten(CompatTools.BuildUmuLaunch(
                    _game.Id, _umuRunPath, home: null, dataHome: _dataHome)));
            }
        }
        catch (OperationCanceledException)
        {
            // 用户取消：安静收场
        }
        catch (Exception ex)
        {
            Save.SetFailure(ex.Message);
        }
        finally
        {
            IsInstallingUmu = false;
        }
    }

    public IReadOnlyList<string> ProtonVersions => _protonVersions;

    [ObservableProperty]
    private string? _selectedProtonVersion;

    /// <summary>Proton 版本变化时触发：用所选版本重新生成命令模板与兼容环境。</summary>
    partial void OnSelectedProtonVersionChanged(string? value)
    {
        if (SelectedLaunchMode?.Mode == LaunchMode.Proton && !string.IsNullOrWhiteSpace(value))
        {
            ApplyGenerated(Flatten(CompatTools.BuildProtonLaunch(
                _game.Id, value, home: null, dataHome: _dataHome)));
        }
    }

    /// <summary>启动方式变化时触发：刷新可见性，按方式生成命令模板并增删兼容环境变量。</summary>
    partial void OnSelectedLaunchModeChanged(LaunchModeOption? value)
    {
        OnPropertyChanged(nameof(IsProtonMode));
        OnPropertyChanged(nameof(IsNativeUmuMode));
        OnPropertyChanged(nameof(IsUmuMode));
        OnPropertyChanged(nameof(IsUmuAvailable));
        OnPropertyChanged(nameof(IsUmuHintVisible));
        RefreshNativeUmuStatus();
        if (value is null)
        {
            return;
        }

        switch (value.Mode)
        {
            case LaunchMode.Direct:
                CommandTemplate = "{exe}";
                RemoveGeneratedEnvironment();
                break;
            case LaunchMode.NativeUmu:
                ApplyGenerated(Flatten(CompatTools.BuildNativeUmuLaunch(
                    _game.Id, home: null, dataHome: _dataHome)));
                break;
            case LaunchMode.Umu:
                ApplyGenerated(Flatten(CompatTools.BuildUmuLaunch(
                    _game.Id, _umuRunPath, home: null, dataHome: _dataHome)));
                break;
            case LaunchMode.Wine:
                ApplyGenerated(Flatten(CompatTools.BuildWineLaunch(
                    _game.Id, _winePath, home: null, dataHome: _dataHome)));
                break;
            case LaunchMode.Proton:
                if (string.IsNullOrWhiteSpace(SelectedProtonVersion) && ProtonVersions.Count > 0)
                {
                    SelectedProtonVersion = ProtonVersions[0]; // 触发生成
                }
                else if (!string.IsNullOrWhiteSpace(SelectedProtonVersion))
                {
                    ApplyGenerated(Flatten(CompatTools.BuildProtonLaunch(
                        _game.Id, SelectedProtonVersion, home: null, dataHome: _dataHome)));
                }
                break;
            case LaunchMode.Custom:
            default:
                break; // 自定义：不动草稿
        }
    }

    /// <summary>CompatLaunch → 生成应用所需的二元组（模板 + 环境变量）。</summary>
    private static (string CommandTemplate, Dictionary<string, string> Environment) Flatten(CompatLaunch launch) =>
        (launch.CommandTemplate, launch.Environment);

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

    /// <summary>从环境文本中移除全部由推荐生成的变量（STEAM_COMPAT_*、umu 系列、WINEPREFIX、PROTONPATH 与游戏推荐项；
    /// 切回直接启动等场景调用，用户手动加的其它变量不受影响）。</summary>
    private void RemoveGeneratedEnvironment()
    {
        var merged = ParseEnvironmentOrEmpty(EnvironmentText);
        var keys = merged.Keys.Where(CompatTools.IsGeneratedEnvironmentKey).ToList();
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

    /// <summary>
    /// 弹系统文件选择对话框选游戏主程序：选中即换算为相对路径（在安装目录内时）写入草稿并保存；
    /// 目录外则存绝对路径。未注册选择器（无头测试/服务缺失）时命令无副作用。
    /// </summary>
    [RelayCommand]
    private async Task BrowseExecutableAsync(CancellationToken cancellationToken)
    {
        Save.Clear();
        if (_filePicker is null)
        {
            return;
        }

        var path = await _filePicker.PickExecutableFileAsync(
            _loc["launch_executablePickTitle"], InstallDirDraft);
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        ExecutableDraft = RelativeToInstallDir(NormalizeDirectoryPath(path));
        await SaveAsync(cancellationToken);
    }

    /// <summary>目录路径统一为正斜杠（与示例配置一致；读取端 Path.GetFullPath 兼容两种斜杠）。</summary>
    private static string NormalizeDirectoryPath(string path) => path.Replace('\\', '/');

    /// <summary>把位于安装目录内的绝对路径换算为相对路径；目录外保持绝对（Path.Combine 对两者都兼容）。</summary>
    private string RelativeToInstallDir(string normalizedPath)
    {
        var root = NormalizeDirectoryPath(InstallDirDraft).TrimEnd('/');
        var separator = root.Length == 0 ? "" : "/";
        return normalizedPath.StartsWith(root + separator, StringComparison.Ordinal)
            ? normalizedPath[(root.Length + separator.Length)..]
            : normalizedPath;
    }

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

        var executable = NormalizeDirectoryPath(ExecutableDraft.Trim());
        if (executable.Length == 0)
        {
            Save.SetFailure(_loc["launch_executableRequired"]);
            return;
        }

        if (!TryParseEnvironment(EnvironmentText, out var environment, out var badLine))
        {
            Save.SetFailure(_loc.Format("launch_invalidEnvLine", badLine));
            return;
        }

        var installDirChanged = !string.Equals(installDir, _owner.InstallDirPath, StringComparison.Ordinal);
        var executableChanged = !string.Equals(executable, _game.Executable, StringComparison.Ordinal);
        if (installDirChanged)
        {
            _game.InstallDir = installDir; // 支持绝对路径，直接写回
        }

        _game.Executable = executable;

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
            else if (executableChanged)
            {
                await _owner.RefreshAsync(cancellationToken); // 可启动性/状态行随之刷新
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
