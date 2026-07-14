using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Threading;
using Elysia.LanDesktopConnect.Services;
using LanMountainDesktop.PluginSdk;

namespace Elysia.LanDesktopConnect.Settings;

public sealed class IpcBridgeSettingsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ElysiaSettingsService _settingsService;
    private readonly ElysiaGatewayController _controller;
    private readonly BunProcessManager _bunManager;
    private readonly IpcMessageRouter _router;
    private readonly PluginLocalizer _localizer;
    private readonly IDisposable _appsSubscription;
    private readonly DispatcherTimer _uptimeTimer;

    private bool _isLoadingSettings;
    private bool _isCheckingBun;
    private bool _isAutoPort = true;
    private int _manualPort = 34567;
    private bool _autoRestart = true;
    private LogLevelOption _selectedLogLevel;
    private bool _debugMode;
    private string? _lastError;
    private bool _disposed;

    public IpcBridgeSettingsViewModel(
        ElysiaSettingsService settingsService,
        ElysiaGatewayController controller,
        IpcMessageRouter router,
        IPluginMessageBus messageBus,
        PluginLocalizer localizer)
    {
        _settingsService = settingsService;
        _controller = controller;
        _bunManager = controller.ProcessManager;
        _router = router;
        _localizer = localizer;

        LogLevels =
        [
            new LogLevelOption("debug", T("settings.log_level.debug", "调试")),
            new LogLevelOption("info", T("settings.log_level.info", "信息")),
            new LogLevelOption("warn", T("settings.log_level.warn", "警告")),
            new LogLevelOption("error", T("settings.log_level.error", "错误"))
        ];
        _selectedLogLevel = LogLevels[1];

        CheckBunCommand = new AsyncRelayCommand(CheckBunAsync);
        InstallBunCommand = new RelayCommand(ShowInstallGuide);
        ToggleGatewayCommand = new AsyncRelayCommand(ToggleGatewayAsync);
        RestartGatewayCommand = new AsyncRelayCommand(RestartGatewayAsync);
        ViewLogsCommand = new RelayCommand(ViewLogs);
        RefreshAppsCommand = new RelayCommand(RefreshApps);
        ResetSettingsCommand = new RelayCommand(ResetSettings);
        ClearErrorCommand = new RelayCommand(() => LastError = null);

        _bunManager.StatusChanged += OnBunStatusChanged;
        _bunManager.PropertyChanged += OnBunManagerPropertyChanged;
        _appsSubscription = messageBus.Subscribe<ConnectedAppsChangedEvent>(message =>
            RunOnUiThread(() => ReplaceConnectedApps(message.Apps)));

        _uptimeTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            if (IsGatewayRunning)
            {
                OnPropertyChanged(nameof(GatewayUptimeText));
            }
        });
        _uptimeTimer.Start();

        LoadSettings();
        RefreshApps();
    }

    public ObservableCollection<ConnectedAppViewModel> ConnectedApps { get; } = [];

    public IReadOnlyList<LogLevelOption> LogLevels { get; }

    public ICommand CheckBunCommand { get; }

    public ICommand InstallBunCommand { get; }

    public ICommand ToggleGatewayCommand { get; }

    public ICommand RestartGatewayCommand { get; }

    public ICommand ViewLogsCommand { get; }

    public ICommand RefreshAppsCommand { get; }

    public ICommand ResetSettingsCommand { get; }

    public ICommand ClearErrorCommand { get; }

    public ICommand RefreshCommand => CheckBunCommand;

    public string PageDescription => T(
        "settings.page_intro",
        "配置仅监听本机的 Elysia IPC 网关，管理 Bun 运行时和外部应用连接。");

    public string BunHeader => T("settings.bun.header", "Bun 运行时");

    public string GatewayHeader => T("settings.gateway.header", "网关服务");

    public string ConnectedAppsHeader => T("settings.connected_apps.header", "已连接应用");

    public string AdvancedHeader => T("settings.advanced.header", "高级设置");

    public string AdvancedDescription => T("settings.advanced.description", "配置端口、重启和日志选项");

    public string InstallGuideText => T("bun.install_guide", "安装指南");

    public string ReadyText => T("bun.ready", "已就绪");

    public string VersionLabel => T("settings.bun.version", "版本");

    public string PathLabel => T("settings.bun.path", "路径");

    public string CheckAgainText => T("settings.bun.check_again", "重新检测");

    public string RestartText => T("gateway.action.restart", "重启");

    public string PortLabel => T("gateway.port", "监听端口");

    public string UptimeLabel => T("gateway.uptime", "运行时间");

    public string ConnectedAppsLabel => T("gateway.connected_apps", "已连接应用");

    public string ViewLogsText => T("gateway.view_logs", "查看日志");

    public string RefreshText => T("settings.refresh", "刷新");

    public string PortConfigurationLabel => T("settings.port.header", "端口配置");

    public string PortConfigurationDescription => T("settings.port.description", "选择自动分配或手动指定本机端口");

    public string AutoPortText => T("settings.port.auto", "自动");

    public string ManualPortText => T("settings.port.manual", "手动");

    public string AutoRestartLabel => T("settings.auto_restart", "自动重启");

    public string AutoRestartDescription => T("settings.auto_restart.description", "网关异常退出后最多自动重启 5 次");

    public string LogLevelLabel => T("settings.log_level", "日志级别");

    public string DebugModeLabel => T("settings.debug_mode", "调试模式");

    public string DebugModeDescription => T("settings.debug_mode.description", "将 Bun 依赖恢复等诊断信息写入日志");

    public string ResetText => T("settings.reset", "恢复默认设置");

    public bool IsCheckingBun
    {
        get => _isCheckingBun;
        private set
        {
            if (SetField(ref _isCheckingBun, value))
            {
                OnPropertyChanged(nameof(BunStatusDescription));
                OnPropertyChanged(nameof(CanToggleGateway));
            }
        }
    }

    public bool IsBunInstalled => !string.IsNullOrWhiteSpace(_bunManager.BunPath);

    public bool IsBunNotInstalled => !IsCheckingBun && !IsBunInstalled;

    public string BunVersion => _bunManager.BunVersion ?? T("common.unknown", "未知");

    public string BunPath => _bunManager.BunPath ?? string.Empty;

    public string BunStatusDescription => IsCheckingBun
        ? T("bun.checking", "正在检测...")
        : IsBunInstalled
            ? _localizer.Format("bun.version_format", "版本 {0}", BunVersion)
            : T("bun.not_installed", "未安装，需要安装 Bun 才能启动 IPC 网关");

    public string BunStatusIcon => IsBunInstalled ? "CheckmarkCircle" : "ErrorCircle";

    public string GatewayStatusDescription => _bunManager.Status switch
    {
        BunStatus.Running => _localizer.Format(
            "gateway.status.running_port",
            "运行中，端口 {0}",
            _bunManager.GatewayPort ?? 0),
        BunStatus.Stopped => T("gateway.status.stopped", "已停止"),
        BunStatus.Starting => T("gateway.status.starting", "正在启动..."),
        BunStatus.Error => T("gateway.status.error", "发生错误"),
        BunStatus.NotInstalled => T("bun.not_installed", "未安装 Bun"),
        _ => T("gateway.status.not_started", "尚未启动")
    };

    public string GatewayStatusIcon => _bunManager.Status switch
    {
        BunStatus.Running => "PlugConnected",
        BunStatus.Stopped => "PlugDisconnected",
        BunStatus.Starting => "ArrowSync",
        BunStatus.Error => "ErrorCircle",
        _ => "QuestionCircle"
    };

    public bool IsGatewayRunning => _bunManager.Status == BunStatus.Running;

    public bool CanToggleGateway => IsBunInstalled && !IsCheckingBun && _bunManager.Status != BunStatus.Starting;

    public string ToggleGatewayButtonText => IsGatewayRunning
        ? T("gateway.action.stop", "停止")
        : T("gateway.action.start", "启动");

    public string GatewayPortText => _bunManager.GatewayPort?.ToString() ?? "-";

    public string GatewayUptimeText
    {
        get
        {
            if (!IsGatewayRunning || _bunManager.StartTime is not { } startTime)
            {
                return "-";
            }

            var uptime = DateTimeOffset.Now - startTime;
            return uptime.TotalHours >= 1
                ? _localizer.Format("gateway.uptime.hours", "{0} 小时 {1} 分钟", (int)uptime.TotalHours, uptime.Minutes)
                : _localizer.Format("gateway.uptime.minutes", "{0} 分钟 {1} 秒", uptime.Minutes, uptime.Seconds);
        }
    }

    public bool HasConnectedApps => ConnectedApps.Count > 0;

    public string ConnectedAppsDescription => HasConnectedApps
        ? _localizer.Format("gateway.connected_apps.count_description", "当前有 {0} 个应用连接", ConnectedApps.Count)
        : T("gateway.connected_apps.empty", "暂无应用连接");

    public string ConnectedAppsCountText => _localizer.Format(
        "gateway.connected_apps.count",
        "{0} 个",
        ConnectedApps.Count);

    public bool IsAutoPort
    {
        get => _isAutoPort;
        set
        {
            if (SetField(ref _isAutoPort, value) && !_isLoadingSettings)
            {
                SaveSettings(nameof(ElysiaSettings.IsAutoPort));
            }
        }
    }

    public bool IsManualPort
    {
        get => !IsAutoPort;
        set
        {
            if (value)
            {
                IsAutoPort = false;
            }
        }
    }

    public int ManualPortNumber
    {
        get => _manualPort;
        set
        {
            var normalized = Math.Clamp(value, 1024, 65535);
            if (SetField(ref _manualPort, normalized) && !_isLoadingSettings)
            {
                SaveSettings(nameof(ElysiaSettings.ManualPort));
            }
        }
    }

    public bool AutoRestart
    {
        get => _autoRestart;
        set
        {
            if (SetField(ref _autoRestart, value) && !_isLoadingSettings)
            {
                SaveSettings(nameof(ElysiaSettings.AutoRestart));
            }
        }
    }

    public LogLevelOption SelectedLogLevel
    {
        get => _selectedLogLevel;
        set
        {
            if (value is not null && SetField(ref _selectedLogLevel, value) && !_isLoadingSettings)
            {
                SaveSettings(nameof(ElysiaSettings.LogLevel));
            }
        }
    }

    public bool DebugMode
    {
        get => _debugMode;
        set
        {
            if (SetField(ref _debugMode, value) && !_isLoadingSettings)
            {
                SaveSettings(nameof(ElysiaSettings.DebugMode));
            }
        }
    }

    public string? LastError
    {
        get => _lastError;
        private set
        {
            if (SetField(ref _lastError, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(LastError);

    public async Task RefreshAsync()
    {
        await CheckBunAsync().ConfigureAwait(true);
        RefreshApps();
    }

    private async Task CheckBunAsync()
    {
        IsCheckingBun = true;
        LastError = null;
        try
        {
            await _controller.DetectBunAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        finally
        {
            IsCheckingBun = false;
            RaiseBunPropertiesChanged();
        }
    }

    private void ShowInstallGuide()
    {
        OpenWithShell("https://bun.sh/docs/installation");
    }

    private async Task ToggleGatewayAsync()
    {
        LastError = null;
        try
        {
            if (IsGatewayRunning)
            {
                await _controller.StopAsync().ConfigureAwait(true);
            }
            else if (!await _controller.StartAsync().ConfigureAwait(true))
            {
                LastError = T("bun.not_installed", "未检测到 Bun，无法启动网关。");
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private async Task RestartGatewayAsync()
    {
        LastError = null;
        try
        {
            await _controller.RestartAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private void ViewLogs()
    {
        var logPath = Path.Combine(_bunManager.DataDirectory, "elysia-gateway.log");
        if (File.Exists(logPath))
        {
            OpenWithShell(logPath);
        }
    }

    private void RefreshApps()
    {
        ReplaceConnectedApps(_router.GetConnectedApps());
    }

    private void ReplaceConnectedApps(IReadOnlyList<ConnectedAppInfo> apps)
    {
        ConnectedApps.Clear();
        foreach (var app in apps)
        {
            ConnectedApps.Add(new ConnectedAppViewModel(app));
        }

        OnPropertyChanged(nameof(HasConnectedApps));
        OnPropertyChanged(nameof(ConnectedAppsDescription));
        OnPropertyChanged(nameof(ConnectedAppsCountText));
    }

    private void ResetSettings()
    {
        _isLoadingSettings = true;
        try
        {
            IsAutoPort = true;
            ManualPortNumber = 34567;
            AutoRestart = true;
            SelectedLogLevel = LogLevels.First(option => option.Code == "info");
            DebugMode = false;
        }
        finally
        {
            _isLoadingSettings = false;
        }

        SaveSettings(
            nameof(ElysiaSettings.IsAutoPort),
            nameof(ElysiaSettings.ManualPort),
            nameof(ElysiaSettings.AutoRestart),
            nameof(ElysiaSettings.LogLevel),
            nameof(ElysiaSettings.DebugMode));
    }

    private void LoadSettings()
    {
        var settings = _settingsService.GetSettings();
        _isLoadingSettings = true;
        try
        {
            IsAutoPort = settings.IsAutoPort;
            ManualPortNumber = settings.ManualPort;
            AutoRestart = settings.AutoRestart;
            SelectedLogLevel = LogLevels.FirstOrDefault(option => option.Code == settings.LogLevel) ?? LogLevels[1];
            DebugMode = settings.DebugMode;
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    private void SaveSettings(params string[] changedKeys)
    {
        _settingsService.UpdateSettings(settings =>
        {
            settings.IsAutoPort = IsAutoPort;
            settings.ManualPort = ManualPortNumber;
            settings.AutoRestart = AutoRestart;
            settings.LogLevel = SelectedLogLevel.Code;
            settings.DebugMode = DebugMode;
        }, changedKeys);
    }

    private void OnBunStatusChanged(object? sender, BunStatus status)
    {
        RunOnUiThread(() =>
        {
            OnPropertyChanged(nameof(GatewayStatusDescription));
            OnPropertyChanged(nameof(GatewayStatusIcon));
            OnPropertyChanged(nameof(IsGatewayRunning));
            OnPropertyChanged(nameof(CanToggleGateway));
            OnPropertyChanged(nameof(ToggleGatewayButtonText));
            OnPropertyChanged(nameof(GatewayUptimeText));
            RaiseBunPropertiesChanged();
        });
    }

    private void OnBunManagerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        RunOnUiThread(() =>
        {
            if (e.PropertyName is nameof(BunProcessManager.BunPath) or nameof(BunProcessManager.BunVersion))
            {
                RaiseBunPropertiesChanged();
            }

            if (e.PropertyName == nameof(BunProcessManager.GatewayPort))
            {
                OnPropertyChanged(nameof(GatewayPortText));
                OnPropertyChanged(nameof(GatewayStatusDescription));
            }

            if (e.PropertyName == nameof(BunProcessManager.StartTime))
            {
                OnPropertyChanged(nameof(GatewayUptimeText));
            }
        });
    }

    private void RaiseBunPropertiesChanged()
    {
        OnPropertyChanged(nameof(IsBunInstalled));
        OnPropertyChanged(nameof(IsBunNotInstalled));
        OnPropertyChanged(nameof(BunVersion));
        OnPropertyChanged(nameof(BunPath));
        OnPropertyChanged(nameof(BunStatusDescription));
        OnPropertyChanged(nameof(BunStatusIcon));
        OnPropertyChanged(nameof(CanToggleGateway));
    }

    private static void OpenWithShell(string target)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = target,
            UseShellExecute = true
        });
    }

    private static void RunOnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    private string T(string key, string fallback) => _localizer.GetString(key, fallback);

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        if (propertyName == nameof(IsAutoPort))
        {
            OnPropertyChanged(nameof(IsManualPort));
        }

        return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _uptimeTimer.Stop();
        _appsSubscription.Dispose();
        _bunManager.StatusChanged -= OnBunStatusChanged;
        _bunManager.PropertyChanged -= OnBunManagerPropertyChanged;
        _disposed = true;
    }
}

public sealed record LogLevelOption(string Code, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed class ConnectedAppViewModel
{
    public ConnectedAppViewModel(ConnectedAppInfo app)
    {
        AppId = app.Id;
        AppName = app.Name;
        AppType = app.Type;
        ConnectedAt = app.ConnectedAt;
        Capabilities = app.Capabilities;
    }

    public string AppId { get; }

    public string AppName { get; }

    public string AppType { get; }

    public IReadOnlyList<string> Capabilities { get; }

    public string AppTypeIcon => AppType.ToLowerInvariant() switch
    {
        "tauri" => "WindowApps",
        "web" => "Globe",
        "rust" => "Code",
        "node" => "Code",
        _ => "PlugConnected"
    };

    public string AppDetails => $"{AppType} • {AppId}";

    public DateTimeOffset ConnectedAt { get; }

    public string ConnectedTime => ConnectedAt.LocalDateTime.ToString("HH:mm:ss");
}

internal sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => execute();

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }
}

internal sealed class AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool _isExecuting;

    public bool CanExecute(object? parameter) => !_isExecuting && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isExecuting = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await execute().ConfigureAwait(true);
        }
        finally
        {
            _isExecuting = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? CanExecuteChanged;
}
