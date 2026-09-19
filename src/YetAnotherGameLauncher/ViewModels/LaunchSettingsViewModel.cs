using System.ComponentModel;
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
/// （DW/GE/UMU-Proton，代号写入 PROTONPATH 并即时落盘，启动/组件准备只下载所选发行版）；
/// 发行版旁可检查上游更新（有新版弹确认覆盖层，确认后更新并清理旧版本）。
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

    /// <summary>语言切换处理器（存字段以支持退订，见 DetachEventSubscriptions）。</summary>
    private readonly PropertyChangedEventHandler _locPropertyChanged;

    /// <summary>
    /// 语言切换：刷新构造期取词的快照——组件状态文案与 LaunchModes 下拉选项。
    /// LaunchModes 元素在构造期取词，语言切换后必须重建；ComboBox SelectedItem 按引用匹配，
    /// 同步把选中项重指到新集合内的实例，游离引用会显示为空白。
    /// </summary>
    private void OnLocPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not ("Item[]" or "Item" or null))
        {
            return;
        }

        OnPropertyChanged(nameof(ProtonCheckButtonText));
        var selectedMode = SelectedLaunchMode?.Mode;
        _launchModes = BuildLaunchModes();
        OnPropertyChanged(nameof(LaunchModes));
        if (selectedMode is { } mode)
        {
            SelectedLaunchMode = _launchModes.First(m => m.Mode == mode);
        }

        RefreshNativeUmuStatus();
    }

    /// <summary>
    /// 退订应用级单例 ILocalizationService 的事件。internal 供 GameItemViewModel 在
    /// RebuildGames 废弃旧列表时级联调用（同程序集 internal）。
    /// </summary>
    internal void DetachEventSubscriptions() => _loc.PropertyChanged -= _locPropertyChanged;

    public LaunchSettingsViewModel(
        GameDefinition game,
        string installDir,
        GameCatalogService catalogService,
        ILocalizationService loc,
        GameItemViewModel owner,
        IFilePickerService? filePicker = null,
        IPlatformInfo? platformInfo = null,
        IReadOnlyList<string>? protonVersions = null,
        string? winePath = null,
        string? dataHome = null,
        IUmuComponentProvisioner? umuProvisioner = null)
    {
        _game = game;
        _owner = owner;
        _catalogService = catalogService;
        _loc = loc;
        _filePicker = filePicker;
        _platform = platformInfo ?? PlatformInfoFactory.Create();
        _installDirDraft = installDir;
        _executableDraft = game.Executable;
        _protonVersions = protonVersions ?? (_platform.IsLinux ? CompatTools.FindProtonVersions() : []);
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
        // 计算属性里的文案不经 Loc[key] 绑定索引器，语言切换需手动刷新；
        // 处理器存字段以便 DetachEventSubscriptions 退订——本 VM 懒创建且随 RebuildGames 整批废弃，
        // 匿名订阅会被应用级单例 ILocalizationService 的委托整批钉住无法回收（2026-09-20 复审结构性消除）
        _locPropertyChanged = OnLocPropertyChanged;
        _loc.PropertyChanged += _locPropertyChanged;

        // Linux 默认最佳配置：裸 {exe} 无法运行 Windows 客户端 → 应用社区推荐链（进草稿，保存后才落盘；
        // 用户已有任何自定义模板则完全不动）
        if (IsLinux && _selectedLaunchMode.Mode == LaunchMode.Direct)
        {
            var launch = CompatTools.BuildRecommendedLaunch(
                _game.Id, _protonVersions, _platform.IsNvidiaGpuPresent,
                dataHome: _dataHome, winePath: resolvedWinePath,
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

    /// <summary>启动方式选项取词（构造期与语言切换共用，语言切换时重建）。</summary>
    private static IReadOnlyList<LaunchModeOption> BuildLaunchModes() =>
    [
        new(LaunchMode.NativeUmu, LocBridge.Instance["launch_mode_native_umu"]),
        new(LaunchMode.Direct, LocBridge.Instance["launch_mode_direct"]),
    ];

    private IReadOnlyList<LaunchModeOption> _launchModes = BuildLaunchModes();

    /// <summary>启动方式选项（umu 在前作为推荐默认；仅 Linux 面板内展示）。语言切换时整体重建。</summary>
    public IReadOnlyList<LaunchModeOption> LaunchModes => _launchModes;

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
        CompatTools.ResolveNativeProtonRequest(ParseEnvironmentOrEmpty(EnvironmentText));

    /// <summary>Proton 本地不存在或清单不可读时的回退 Runtime（steamrt4，最新 UMU/GE-Proton 所需）。</summary>
    private static (string Variant, string Name) FallbackRuntime =>
        (SteamRuntimeCatalog.Default.Variant, SteamRuntimeCatalog.Default.Name);

    /// <summary>
    /// 刷新组件状态：只认与启动请求完全一致的 Proton（绝对路径 / 版本名 / 代号前缀最新），
    /// Runtime 按 Proton 的 toolmanifest 实际声明解析；Proton 本地不存在时按默认（steamrt4，最新 UMU/GE-Proton 所需）近似。
    /// </summary>
    private void RefreshNativeUmuStatus()
    {
        // umu 面板仅在 Linux 可见（GameSettingsPage IsVisible 门控），Windows 上别做
        // 只读扫描空转——非 Linux 直接落"未检查"态即可
        if (!IsNativeUmuMode || !IsLinux || _umuProvisioner is null)
        {
            NativeUmuStatusText = "";
            OnPropertyChanged(nameof(CanPrepareUmuComponents));
            OnPropertyChanged(nameof(CanCheckProtonUpdate));
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
        OnPropertyChanged(nameof(CanCheckProtonUpdate));
    }

    private bool IsProtonRequestReady(string protonRequest)
    {
        if (_umuProvisioner is null)
        {
            return false;
        }

        // 统一走准备器的本地解析（绝对路径/版本名/代号前缀最新），与启动路径同一规则——
        // 不用构造期 _protonVersions 快照，更新/清理旧版后状态即时准确
        return Path.IsPathRooted(protonRequest)
            ? _umuProvisioner.IsProtonReady(protonRequest)
            : _umuProvisioner.FindInstalledProton(protonRequest) is not null;
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

    /// <summary>Proton 更新检查状态（驱动检查按钮文案与可用性）。</summary>
    [ObservableProperty]
    private ProtonUpdateCheckState _protonUpdateState;

    /// <summary>检测到的上游新版本 tag（UpdateAvailable / Updating 状态下有值）。</summary>
    [ObservableProperty]
    private string? _pendingProtonUpdateTag;

    /// <summary>是否显示 Proton 更新确认覆盖层。</summary>
    [ObservableProperty]
    private bool _showProtonUpdateConfirm;

    /// <summary>更新确认覆盖层正文（弹出时计算，含新旧版本名）。</summary>
    [ObservableProperty]
    private string _protonUpdateConfirmMessage = "";

    /// <summary>检查更新按钮文案：空闲"检查更新"，检测到新版本后"更新到 {tag}"。</summary>
    public string ProtonCheckButtonText =>
        ProtonUpdateState is ProtonUpdateCheckState.UpdateAvailable or ProtonUpdateCheckState.Updating
        && !string.IsNullOrEmpty(PendingProtonUpdateTag)
            ? _loc.Format("launch_proton_update_to", PendingProtonUpdateTag!)
            : _loc["launch_proton_check_update"];

    /// <summary>是否可点检查/更新按钮（查询与下载进行中禁用）。</summary>
    public bool CanCheckProtonUpdate =>
        IsNativeUmuMode && IsLinux && _umuProvisioner is not null
        && ProtonUpdateState is not (ProtonUpdateCheckState.Checking or ProtonUpdateCheckState.Updating);

    /// <summary>更新查询或下载进行中（覆盖层确认钮防重入）。</summary>
    public bool IsProtonUpdateInProgress =>
        ProtonUpdateState is ProtonUpdateCheckState.Checking or ProtonUpdateCheckState.Updating;

    partial void OnProtonUpdateStateChanged(ProtonUpdateCheckState value)
    {
        OnPropertyChanged(nameof(ProtonCheckButtonText));
        OnPropertyChanged(nameof(CanCheckProtonUpdate));
        OnPropertyChanged(nameof(IsProtonUpdateInProgress));
    }

    partial void OnPendingProtonUpdateTagChanged(string? value)
        => OnPropertyChanged(nameof(ProtonCheckButtonText));

    /// <summary>当前发行版请求（检查更新/更新的目标；下拉框值为代号，缺省回 UI 默认）。</summary>
    private string SelectedProtonRequest => SelectedProtonFlavor ?? CompatTools.DefaultProtonFlavor;

    /// <summary>查询所选发行版的上游最新 tag；本地不落后即提示已是最新，否则弹确认覆盖层并切换按钮为更新。</summary>
    [RelayCommand]
    private async Task CheckProtonUpdateAsync(CancellationToken cancellationToken)
    {
        if (_umuProvisioner is null || !IsLinux || !IsNativeUmuMode
            || ProtonUpdateState is ProtonUpdateCheckState.Checking or ProtonUpdateCheckState.Updating)
        {
            return;
        }

        ProtonUpdateState = ProtonUpdateCheckState.Checking;
        Save.Clear();
        try
        {
            var tag = await _umuProvisioner
                .FetchLatestProtonTagAsync(SelectedProtonRequest, cancellationToken);
            var local = _umuProvisioner.FindInstalledProton(SelectedProtonRequest);
            var localName = LocalProtonName(local);
            if (localName is not null && !IsUpstreamNewer(localName, tag))
            {
                ProtonUpdateState = ProtonUpdateCheckState.Idle;
                Save.SetSuccess(_loc.Format("launch_proton_up_to_date", localName));
                return;
            }

            PendingProtonUpdateTag = tag;
            // 删旧版对运行中的游戏有风险（延迟加载的 .so 失效），确认文案明示先退出。
            // 版本号 token 过连字符插入 WORD JOINER，窄卡内换行不断在 token 中间
            // （截图评审实锤：GE-Proton11-6 曾被拆成"GE-"行尾 + "Proton11-6"次行）
            ProtonUpdateConfirmMessage = _loc.Format(
                "launch_proton_update_confirm",
                KeepWholeToken(tag),
                KeepWholeToken(localName ?? _loc["launch_proton_not_installed"]))
                + "\n" + _loc["launch_proton_update_running_hint"];
            ProtonUpdateState = ProtonUpdateCheckState.UpdateAvailable;
            ShowProtonUpdateConfirm = true;
        }
        catch (OperationCanceledException)
        {
            ProtonUpdateState = ProtonUpdateCheckState.Idle;
        }
        catch (Exception ex)
        {
            ProtonUpdateState = ProtonUpdateCheckState.Idle;
            Save.SetFailure(ex.Message);
        }
    }

    /// <summary>确认更新：下载所选发行版最新版并清理旧版本；完成后按钮回到"检查更新"。</summary>
    [RelayCommand]
    private async Task ConfirmProtonUpdateAsync(CancellationToken cancellationToken)
    {
        if (_umuProvisioner is null || ProtonUpdateState != ProtonUpdateCheckState.UpdateAvailable)
        {
            return;
        }

        ProtonUpdateState = ProtonUpdateCheckState.Updating;
        ShowProtonUpdateConfirm = false;
        Save.Clear();
        try
        {
            var progress = new Progress<string>(msg =>
            {
                Save.Clear();
                Save.SetSuccess(msg);
            });
            var newPath = await _umuProvisioner
                .UpdateProtonAsync(SelectedProtonRequest, progress, cancellationToken);
            var newName = LocalProtonName(newPath) ?? newPath;
            PendingProtonUpdateTag = null;
            ProtonUpdateState = ProtonUpdateCheckState.Idle;
            Save.Clear();
            Save.SetSuccess(_loc.Format("launch_proton_updated", newName));
            RefreshNativeUmuStatus();
        }
        catch (OperationCanceledException)
        {
            // 取消后保持"更新"可用态（新版本信息仍在）
            ProtonUpdateState = PendingProtonUpdateTag is null
                ? ProtonUpdateCheckState.Idle
                : ProtonUpdateCheckState.UpdateAvailable;
        }
        catch (Exception ex)
        {
            Save.SetFailure(ex.Message);
            ProtonUpdateState = ProtonUpdateCheckState.UpdateAvailable;
            RefreshNativeUmuStatus();
        }
    }

    /// <summary>关闭更新确认覆盖层（新版本信息保留，按钮保持"更新到 {tag}"随时可再更新）。</summary>
    [RelayCommand]
    private void CancelProtonUpdate() => ShowProtonUpdateConfirm = false;

    /// <summary>本地 Proton 绝对路径 → 版本目录名；null/空路径返回 null。</summary>
    private static string? LocalProtonName(string? protonPath) =>
        string.IsNullOrWhiteSpace(protonPath)
            ? null
            : Path.GetFileName(protonPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>上游 tag 是否比本地版本新（数字段自然序，单一事实源 CompatTools.NumericSortKey）。</summary>
    private static bool IsUpstreamNewer(string localName, string tag) =>
        string.CompareOrdinal(CompatTools.NumericSortKey(tag), CompatTools.NumericSortKey(localName)) > 0;

    /// <summary>版本号过每个连字符插入 WORD JOINER（U+2060，零宽不可见）：
    /// 换行只在空白/标点处发生，版本号 token 保持完整（HarfBuzz 对 default-ignorable 不渲染字形）。</summary>
    private static string KeepWholeToken(string token) => token.Replace("-", "-\u2060");

    /// <summary>umu 启动可选的 Proton 发行版代号（显示名即代号；DW 在前为默认）。</summary>
    public IReadOnlyList<string> ProtonFlavors => CompatTools.ProtonFlavors;

    [ObservableProperty]
    private string? _selectedProtonFlavor;

    /// <summary>Proton 发行版变化时触发：代号写入环境草稿的 PROTONPATH 并即时落盘——
    /// 启动链读的是已保存 env，只写草稿不保存会出现"显示 GE-Proton、实际按旧配置启动"的错位。</summary>
    partial void OnSelectedProtonFlavorChanged(string? value)
    {
        if (IsNativeUmuMode && !string.IsNullOrWhiteSpace(value))
        {
            MergeEnvironmentVariable("PROTONPATH", value);
            RefreshNativeUmuStatus();
            _ = SaveSelectedFlavorAsync();
        }
    }

    /// <summary>
    /// 把单个环境变量合并进编辑框草稿：可解析行按键合并（已存在原位覆盖、新键按输入顺序追加），
    /// 无法解析的行（用户输入到一半、还没有 "=" 的半行）原样保留——即时落盘不得吞掉正在输入的内容
    /// （2026-09-20 复审修复；此前 Serialize(Parse(text)) 会无声丢弃半行）。
    /// </summary>
    private void MergeEnvironmentVariable(string key, string value)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        var preserved = new List<string>();
        foreach (var raw in EnvironmentText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryParseEnvironment(raw, out var single, out _))
            {
                foreach (var (k, v) in single)
                {
                    merged[k] = v;
                }
            }
            else
            {
                preserved.Add(raw);
            }
        }

        merged[key] = value;
        EnvironmentText = string.Join(
            Environment.NewLine, merged.Select(kv => $"{kv.Key}={kv.Value}").Concat(preserved));
    }

    /// <summary>把发行版选择立即保存回 games.json（选择即生效）。可预期失败已由 SaveAsync 写入消息槽；
    /// 此处兜底捕获防未观察任务异常。</summary>
    private async Task SaveSelectedFlavorAsync()
    {
        try
        {
            await SaveAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            // SaveAsync 消息槽已提示用户；静默防崩
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
                    _game.Id, dataHome: _dataHome, umuId: _game.Launch.UmuId,
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

    /// <summary>宽松解析环境文本为字典（跳过无 "=" 的行，不报错）；行级解析复用严格版，保证切分规则单一。
    /// internal 供单测（经 InternalsVisibleTo）。</summary>
    internal static Dictionary<string, string> ParseEnvironmentOrEmpty(string text)
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
    internal static string NormalizeDirectoryPath(string path) => path.Replace('\\', '/');

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
        var templateChanged = !string.Equals(CommandTemplate.Trim(), _game.Launch.CommandTemplate, StringComparison.Ordinal);
        // 保存时空白工作目录归一为 {installDir}，对比必须按同一规则，否则"留空"永远算变更
        var effectiveWorkingDirectory = string.IsNullOrWhiteSpace(WorkingDirectory) ? "{installDir}" : WorkingDirectory.Trim();
        var workingDirectoryChanged = !string.Equals(effectiveWorkingDirectory, _game.Launch.WorkingDirectory, StringComparison.Ordinal);
        var environmentDiff = EnvironmentDiffKeys(environment, _game.Launch.Environment);

        // games.json 的 launch.umuId 不经设置卡编辑，重建 Launch 时必须保留（否则 UMU_ID 退化为 umu-{gameId}）
        var newLaunch = new LaunchOptions
        {
            CommandTemplate = CommandTemplate.Trim(),
            WorkingDirectory = effectiveWorkingDirectory,
            Environment = environment,
            UmuId = _game.Launch.UmuId,
        };

        // 快照旧值：保存失败时回滚内存中的 GameDefinition，让内存与磁盘保持一致——
        // 目录序列化的就是 _game 本体，失败后若保留新值，此后任何无关落盘
        //（如关窗 PersistWindowState）会把这次"失败"静默持久化（2026-09-20 复审修复）
        var originalInstallDir = _game.InstallDir;
        var originalExecutable = _game.Executable;
        var originalLaunch = _game.Launch;

        if (installDirChanged)
        {
            _game.InstallDir = installDir; // 支持绝对路径，直接写回
        }

        _game.Executable = executable;
        _game.Launch = newLaunch;

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
            RaiseChangedToast(installDirChanged, executableChanged, templateChanged, workingDirectoryChanged, environmentDiff);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _game.InstallDir = originalInstallDir;
            _game.Executable = originalExecutable;
            _game.Launch = originalLaunch;
            Save.SetFailure(_loc.Format("message_saveFailed", ex.Message));
        }
    }

    /// <summary>保存成功且确有字段变更时弹轻提示（逐项列出变更字段；无变更/保存失败静默——
    /// 消息槽已承载结果，重复提示只添噪）。</summary>
    private void RaiseChangedToast(
        bool installDirChanged,
        bool executableChanged,
        bool templateChanged,
        bool workingDirectoryChanged,
        List<string> environmentDiff)
    {
        var labels = new List<string>();
        if (installDirChanged)
        {
            labels.Add(_loc["launch_installDir"]);
        }

        if (executableChanged)
        {
            labels.Add(_loc["launch_executable"]);
        }

        if (templateChanged)
        {
            labels.Add(_loc["launch_commandTemplate"]);
        }

        if (workingDirectoryChanged)
        {
            labels.Add(_loc["launch_workingDirectory"]);
        }

        if (environmentDiff.Count > 0)
        {
            // 仅 PROTONPATH 变化即发行版下拉的"选择即保存"，按 UI 词汇提示而非笼统的环境变量
            labels.Add(environmentDiff.All(key => key == "PROTONPATH")
                ? _loc["launch_protonFlavor"]
                : _loc["launch_environment"]);
        }

        if (labels.Count > 0)
        {
            _owner.RaiseSettingsChangedToast(_loc.Format(
                "toast_settingsUpdated", string.Join(_loc["common_comma"], labels)));
        }
    }

    /// <summary>两组环境变量的差异键集合（序数比较；键增删与值变化都算差异）。</summary>
    private static List<string> EnvironmentDiffKeys(Dictionary<string, string> next, Dictionary<string, string> current)
    {
        var diff = new List<string>();
        foreach (var (key, value) in next)
        {
            if (!current.TryGetValue(key, out var existing)
                || !string.Equals(existing, value, StringComparison.Ordinal))
            {
                diff.Add(key);
            }
        }

        diff.AddRange(current.Keys.Where(key => !next.ContainsKey(key)));
        return diff;
    }

    /// <summary>环境变量字典 → 多行 KEY=VALUE 文本（编辑框显示用）。</summary>
    private static string SerializeEnvironment(Dictionary<string, string> environment)
        => string.Join(Environment.NewLine, environment.Select(kv => $"{kv.Key}={kv.Value}"));

    /// <summary>多行 KEY=VALUE 文本 → 字典；出错的行写入 badLine 返回 false（解析容错与保存校验共用）。
    /// internal 供单测（经 InternalsVisibleTo）。</summary>
    internal static bool TryParseEnvironment(
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

/// <summary>Proton 更新检查状态（检查按钮文案与可用性的驱动源）。</summary>
public enum ProtonUpdateCheckState
{
    /// <summary>空闲：按钮显示"检查更新"。</summary>
    Idle,

    /// <summary>正在查询上游最新版本。</summary>
    Checking,

    /// <summary>检测到新版本：按钮显示"更新到 {tag}"。</summary>
    UpdateAvailable,

    /// <summary>正在下载并安装更新。</summary>
    Updating,
}
