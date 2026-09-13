using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YetAnotherGameLauncher.Core;
using YetAnotherGameLauncher.Core.Abstractions;
using YetAnotherGameLauncher.Core.Models;
using YetAnotherGameLauncher.Core.Services;
using YetAnotherGameLauncher.Core.Services.Umu;
using YetAnotherGameLauncher.Services;

namespace YetAnotherGameLauncher.ViewModels;

/// <summary>
/// 详情页"启动设置"卡：编辑命令模板 / 工作目录 / 环境变量并保存回 games.json。
/// Linux 启动方式二选一（umu 启动 / 直接运行）；umu 模式旁选 Proton 发行版
/// （DW/GE/UMU-Proton，写入 PROTONPATH 代号，组件准备据此拉对应仓库 latest）。
/// 环境变量以多行 KEY=VALUE 文本编辑（解析容错，错误行给出行内容提示）。
/// </summary>
public partial class LaunchSettingsViewModel : ViewModelBase
{
    private readonly GameDefinition _game;
    private readonly GameItemViewModel _owner;
    private readonly GameCatalogService _catalogService;
    private readonly ILocalizationService _loc;
    private readonly IFilePickerService? _filePicker;
    private readonly IPlatformInfo _platform;
    private readonly IReadOnlyList<string> _protonVersions;
    private readonly string _dataHome;
    private readonly IUmuComponentProvisioner? _umuProvisioner;

    public LaunchSettingsViewModel(
        GameDefinition game,
        string installDir,
        GameCatalogService catalogService,
        ILocalizationService loc,
        GameItemViewModel owner,
        IFilePickerService? filePicker = null,
        IPlatformInfo? platformInfo = null,
        IReadOnlyList<string>? protonVersions = null,
        string? umuRunPath = null,
        string? winePath = null,
        string? dataHome = null,
        IUmuComponentProvisioner? umuProvisioner = null)
    {
        _game = game;
        _owner = owner;
        _catalogService = catalogService;
        _loc = loc;
        _filePicker = filePicker;
        _platform = platformInfo ?? (OperatingSystem.IsLinux()
            ? new LinuxPlatformInfo()
            : new WindowsPlatformInfo());
        _installDirDraft = installDir;
        _executableDraft = game.Executable;
        _protonVersions = protonVersions ?? (_platform.IsLinux ? CompatTools.FindProtonVersions() : []);
        // 显式空串 = 声明"没装"（测试禁用真机 PATH 扫描）；null = 现场（仅 Linux）发现
        var resolvedUmuRunPath = umuRunPath is null
            ? (IsLinux ? CompatTools.FindUmuRun() : null)
            : (umuRunPath.Length == 0 ? null : umuRunPath);
        var resolvedWinePath = winePath is null
            ? (IsLinux ? CompatTools.FindSystemWine() : null)
            : (winePath.Length == 0 ? null : winePath);
        _dataHome = dataHome ?? AppPaths.DataHomeDirectory; // CompatTools 语义要求数据根（不含 yagl 后缀）
        _umuProvisioner = umuProvisioner;
        _commandTemplate = game.Launch.CommandTemplate;
        _workingDirectory = game.Launch.WorkingDirectory;
        _environmentText = SerializeEnvironment(game.Launch.Environment);
        _selectedLaunchMode = DetectLaunchMode(game.Launch.CommandTemplate);
        var detectedFlavor = DetectProtonFlavor(game.Launch.Environment.GetValueOrDefault("PROTONPATH"));
        _selectedProtonFlavor = CompatTools.ProtonFlavors.FirstOrDefault(
            f => string.Equals(f, detectedFlavor, StringComparison.Ordinal)) ?? CompatTools.DefaultProtonFlavor;

        // Linux 默认最佳配置：裸 {exe} 无法运行 Windows 客户端 → 应用社区推荐链（进草稿，保存后才落盘；
        // 用户已有任何自定义模板则完全不动）
        if (IsLinux && _selectedLaunchMode.Mode == LaunchMode.Direct)
        {
            var launch = CompatTools.BuildRecommendedLaunch(
                _game.Id, _protonVersions, _platform.IsNvidiaGpuPresent,
                dataHome: _dataHome, umuRunPath: resolvedUmuRunPath, winePath: resolvedWinePath,
                umuId: _game.Launch.UmuId);
            SelectedLaunchMode = LaunchModes.First(m => m.Mode == launch.Mode);
            ApplyGenerated((launch.CommandTemplate, launch.Environment));
        }

        RefreshNativeUmuStatus();
    }

    /// <summary>从命令模板推断当前启动方式：native-umu 标记 token → umu 启动；{exe} 原样 → 直接运行；
    /// 其余（外部 umu-run / wine / Proton 直启 / 自定义）仅作显示映射为 umu 启动，不重写草稿——
    /// 存量模板在用户主动切换启动方式前保持原样运行。
    /// 返回 LaunchModes 集合内的实例——ComboBox 的 SelectedItem 按引用匹配，游离实例会显示为空白。</summary>
    private LaunchModeOption DetectLaunchMode(string commandTemplate)
    {
        var t = commandTemplate.Trim();
        var mode = t.Contains("native-umu", StringComparison.OrdinalIgnoreCase) || t != "{exe}"
            ? LaunchMode.NativeUmu
            : LaunchMode.Direct;
        return LaunchModes.First(m => m.Mode == mode);
    }

    /// <summary>从 PROTONPATH（代号 / 具体版本名 / 绝对路径）反推 Proton 发行版；识别不出回默认 DW-Proton。</summary>
    private static string DetectProtonFlavor(string? protonPath)
    {
        if (!string.IsNullOrWhiteSpace(protonPath))
        {
            var dirName = Path.GetFileName(
                protonPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            foreach (var candidate in new[] { protonPath, dirName })
            {
                if (candidate.StartsWith("UMU", StringComparison.OrdinalIgnoreCase))
                {
                    return "UMU-Proton";
                }

                if (candidate.StartsWith("GE", StringComparison.OrdinalIgnoreCase))
                {
                    return "GE-Proton";
                }

                if (candidate.StartsWith("DW", StringComparison.OrdinalIgnoreCase))
                {
                    return "DW-Proton"; // 代号 DW-Proton / 目录名 dwproton-*、dw-proton-*
                }
            }
        }

        return CompatTools.DefaultProtonFlavor;
    }

    /// <summary>安装目录草稿（绝对路径；与启动参数共用同一保存按钮）。</summary>
    [ObservableProperty]
    private string _installDirDraft;

    /// <summary>游戏可执行文件草稿（相对安装目录或绝对路径，'/' 分隔）。</summary>
    [ObservableProperty]
    private string _executableDraft;

    /// <summary>当前系统是否为 Linux（决定是否显示兼容层选择；平台信息注入，测试可控）。</summary>
    public bool IsLinux => _platform.IsLinux;

    /// <summary>启动方式选项（umu 在前作为推荐默认；仅 Linux 面板内展示）。</summary>
    public IReadOnlyList<LaunchModeOption> LaunchModes { get; } =
    [
        new(LaunchMode.NativeUmu, LocBridge.Instance["launch_mode_native_umu"]),
        new(LaunchMode.Direct, LocBridge.Instance["launch_mode_direct"]),
    ];

    [ObservableProperty]
    private LaunchModeOption? _selectedLaunchMode;

    /// <summary>原生 umu 组件准备是否进行中。</summary>
    [ObservableProperty]
    private bool _isPreparingUmuComponents;

    /// <summary>原生 umu 组件状态摘要（就绪 / 缺 Proton / 缺 Runtime / 未检查）。</summary>
    [ObservableProperty]
    private string _nativeUmuStatusText = "";

    /// <summary>是否可执行「检查/下载兼容组件」（Linux + 原生 umu + 准备器可用）。</summary>
    public bool CanPrepareUmuComponents =>
        IsNativeUmuMode && IsLinux && _umuProvisioner is not null && !IsPreparingUmuComponents;

    /// <summary>解析原生 umu 用的 Proton 请求（与启动路径共用 CompatTools.ResolveNativeProtonRequest）。</summary>
    private string ResolveNativeProtonRequest() =>
        CompatTools.ResolveNativeProtonRequest(ParseEnvironmentOrEmpty(EnvironmentText), _protonVersions);

    /// <summary>Proton 本地不存在或清单不可读时的回退 Runtime（steamrt4，最新 UMU/GE-Proton 所需）。</summary>
    private static (string Variant, string Name) FallbackRuntime =>
        (SteamRuntimeCatalog.Default.Variant, SteamRuntimeCatalog.Default.Name);

    /// <summary>
    /// 刷新组件状态：只认与启动请求完全一致的 Proton（绝对路径 / 版本名 / 代号前缀最新），
    /// Runtime 按 Proton 的 toolmanifest 实际声明解析；Proton 本地不存在时按默认（steamrt4，最新 UMU/GE-Proton 所需）近似。
    /// </summary>
    private void RefreshNativeUmuStatus()
    {
        if (!IsNativeUmuMode || _umuProvisioner is null)
        {
            NativeUmuStatusText = "";
            OnPropertyChanged(nameof(CanPrepareUmuComponents));
            return;
        }

        var protonRequest = ResolveNativeProtonRequest();
        var protonReady = IsProtonRequestReady(protonRequest);
        var (runtimeVariant, _) = _umuProvisioner.ResolveRequiredRuntime(protonRequest) ?? FallbackRuntime;
        var runtimeReady = _umuProvisioner.IsRuntimeReady(runtimeVariant);
        if (protonReady && runtimeReady)
        {
            NativeUmuStatusText = _loc["launch_native_components_ready"];
        }
        else if (protonReady)
        {
            NativeUmuStatusText = _loc["launch_native_runtime_missing"];
        }
        else
        {
            NativeUmuStatusText = _loc["launch_native_components_missing"];
        }

        OnPropertyChanged(nameof(CanPrepareUmuComponents));
    }

    private bool IsProtonRequestReady(string protonRequest)
    {
        if (_umuProvisioner is null)
        {
            return false;
        }

        if (_umuProvisioner.IsProtonReady(protonRequest))
        {
            return true;
        }

        if (Path.IsPathRooted(protonRequest))
        {
            return false;
        }

        var root = UmuPaths.SteamCompatRoot(_dataHome);
        var asName = Path.Combine(root, protonRequest);
        if (_umuProvisioner.IsProtonReady(asName))
        {
            return true;
        }

        // 代号（DW/GE/UMU-Proton，与准备器同一判定）：该发行版前缀下最新已装即可
        if (!CompatTools.IsProtonCodename(protonRequest))
        {
            return false;
        }

        var prefix = protonRequest.StartsWith("UMU", StringComparison.OrdinalIgnoreCase)
            ? "UMU-Proton"
            : protonRequest.StartsWith("GE", StringComparison.OrdinalIgnoreCase)
                ? "GE-Proton"
                : "dwproton";
        return _protonVersions.Any(v =>
            v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && _umuProvisioner.IsProtonReady(Path.Combine(root, v)));
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
                .EnsureProtonAsync(ResolveNativeProtonRequest(), progress, cancellationToken);
            // 下载完成后重读 toolmanifest：Runtime 按刚就位的 Proton 实际声明准备
            var (runtimeVariant, runtimeName) =
                _umuProvisioner.ResolveRequiredRuntime(ResolveNativeProtonRequest()) ?? FallbackRuntime;
            await _umuProvisioner
                .EnsureRuntimeAsync(runtimeVariant, runtimeName, progress, cancellationToken);
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

    /// <summary>是否处于 umu 启动方式（决定 Proton 发行版选择器与组件状态卡可见性）。</summary>
    public bool IsNativeUmuMode => SelectedLaunchMode?.Mode == LaunchMode.NativeUmu;

    /// <summary>umu 启动可选的 Proton 发行版代号（显示名即代号；DW 在前为默认）。</summary>
    public IReadOnlyList<string> ProtonFlavors => CompatTools.ProtonFlavors;

    [ObservableProperty]
    private string? _selectedProtonFlavor;

    /// <summary>Proton 发行版变化时触发：代号写入环境草稿的 PROTONPATH（启动/组件准备按代号拉 latest）。</summary>
    partial void OnSelectedProtonFlavorChanged(string? value)
    {
        if (IsNativeUmuMode && !string.IsNullOrWhiteSpace(value))
        {
            var merged = ParseEnvironmentOrEmpty(EnvironmentText);
            merged["PROTONPATH"] = value;
            EnvironmentText = SerializeEnvironment(merged);
            RefreshNativeUmuStatus();
        }
    }

    /// <summary>启动方式变化时触发：刷新可见性，按方式生成命令模板并增删兼容环境变量。</summary>
    partial void OnSelectedLaunchModeChanged(LaunchModeOption? value)
    {
        OnPropertyChanged(nameof(IsNativeUmuMode));
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
                    _game.Id, home: null, dataHome: _dataHome, umuId: _game.Launch.UmuId,
                    protonFlavor: SelectedProtonFlavor)));
                break;
            default:
                break; // 未在 UI 暴露的存量模式：不动草稿
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
