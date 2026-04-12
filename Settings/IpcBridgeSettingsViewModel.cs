using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Elysia.LanDesktopConnect.Services;
using LanMountainDesktop.PluginSdk;

namespace Elysia.LanDesktopConnect.Settings;

public class IpcBridgeSettingsViewModel : INotifyPropertyChanged
{
    private readonly ElysiaSettingsService _settingsService;
    private readonly BunProcessManager _bunManager;
    private readonly IPluginMessageBus _messageBus;

    public IpcBridgeSettingsViewModel(
        ElysiaSettingsService settingsService,
        BunProcessManager bunManager,
        IPluginMessageBus messageBus)
    {
        _settingsService = settingsService;
        _bunManager = bunManager;
        _messageBus = messageBus;

        _bunManager.StatusChanged += OnBunStatusChanged;
        _bunManager.PropertyChanged += OnBunManagerPropertyChanged;

        CheckBunCommand = new AsyncRelayCommand(CheckBunAsync);
        InstallBunCommand = new RelayCommand(ShowInstallGuide);
        ToggleGatewayCommand = new AsyncRelayCommand(ToggleGatewayAsync);
        RestartGatewayCommand = new AsyncRelayCommand(RestartGatewayAsync);
        ViewLogsCommand = new RelayCommand(ViewLogs);
        RefreshAppsCommand = new RelayCommand(RefreshApps);
        ResetSettingsCommand = new RelayCommand(ResetSettings);

        LoadSettings();
        _ = CheckBunAsync();
    }

    #region Bun 状态

    private BunStatus _bunStatus = BunStatus.NotStarted;
    private string _bunVersion = "";
    private string _bunPath = "";

    public BunStatus BunStatus
    {
        get => _bunStatus;
        set { _bunStatus = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsBunInstalled)); OnPropertyChanged(nameof(IsBunNotInstalled)); OnPropertyChanged(nameof(BunStatusDescription)); OnPropertyChanged(nameof(BunStatusIcon)); }
    }

    public string BunVersion
    {
        get => _bunVersion;
        set { _bunVersion = value; OnPropertyChanged(); OnPropertyChanged(nameof(BunStatusDescription)); }
    }

    public string BunPath
    {
        get => _bunPath;
        set { _bunPath = value; OnPropertyChanged(); }
    }

    public bool IsBunInstalled => BunStatus == BunStatus.Installed;
    public bool IsBunNotInstalled => BunStatus == BunStatus.NotInstalled;

    public string BunStatusDescription => BunStatus switch
    {
        BunStatus.Installed => $"版本 {BunVersion}",
        BunStatus.NotInstalled => "未安装，需要安装才能使用 IPC 功能",
        BunStatus.Checking => "正在检测...",
        _ => "未知状态"
    };

    public string BunStatusIcon => BunStatus == BunStatus.Installed ? "CheckmarkCircle" : "ErrorCircle";

    #endregion

    #region 网关状态

    public string GatewayStatusDescription => _bunManager.Status switch
    {
        BunStatus.Running => $"运行中，端口 {_bunManager.GatewayPort}",
        BunStatus.Stopped => "已停止",
        BunStatus.Starting => "正在启动...",
        BunStatus.Error => "发生错误",
        _ => "未知"
    };

    public string GatewayStatusIcon => _bunManager.Status switch
    {
        BunStatus.Running => "PlugConnected",
        BunStatus.Stopped => "PlugDisconnected",
        BunStatus.Starting => "Sync",
        BunStatus.Error => "ErrorCircle",
        _ => "Help"
    };

    public bool IsGatewayRunning => _bunManager.Status == BunStatus.Running;
    public bool CanToggleGateway => IsBunInstalled && _bunManager.Status != BunStatus.Starting;
    public string ToggleGatewayButtonText => IsGatewayRunning ? "停止" : "启动";

    public string GatewayPortText => _bunManager.GatewayPort?.ToString() ?? "-";

    public string GatewayUptimeText
    {
        get
        {
            if (!IsGatewayRunning || !_bunManager.StartTime.HasValue)
                return "-";

            var uptime = DateTime.Now - _bunManager.StartTime.Value;
            return uptime.TotalHours >= 1
                ? $"{(int)uptime.TotalHours}小时 {uptime.Minutes}分钟"
                : $"{uptime.Minutes}分钟 {uptime.Seconds}秒";
        }
    }

    #endregion

    #region 已连接应用

    public ObservableCollection<ConnectedAppViewModel> ConnectedApps { get; } = new();

    public bool HasConnectedApps => ConnectedApps.Count > 0;

    public string ConnectedAppsDescription => HasConnectedApps
        ? $"当前有 {ConnectedApps.Count} 个应用连接"
        : "暂无应用连接";

    public string ConnectedAppsCountText => $"{ConnectedApps.Count} 个";

    #endregion

    #region 设置

    private bool _isAutoPort = true;
    private int _manualPort = 34567;
    private bool _autoRestart = true;
    private string _selectedLogLevel = "信息";
    private bool _debugMode;

    public bool IsAutoPort
    {
        get => _isAutoPort;
        set { _isAutoPort = value; OnPropertyChanged(); SaveSettings(); }
    }

    public int ManualPortNumber
    {
        get => _manualPort;
        set { _manualPort = value; OnPropertyChanged(); SaveSettings(); }
    }

    public bool AutoRestart
    {
        get => _autoRestart;
        set { _autoRestart = value; OnPropertyChanged(); SaveSettings(); }
    }

    public string[] LogLevels { get; } = { "调试", "信息", "警告", "错误" };

    public string SelectedLogLevel
    {
        get => _selectedLogLevel;
        set { _selectedLogLevel = value; OnPropertyChanged(); SaveSettings(); }
    }

    public bool DebugMode
    {
        get => _debugMode;
        set { _debugMode = value; OnPropertyChanged(); SaveSettings(); }
    }

    #endregion

    #region 命令

    public ICommand CheckBunCommand { get; }
    public ICommand InstallBunCommand { get; }
    public ICommand ToggleGatewayCommand { get; }
    public ICommand RestartGatewayCommand { get; }
    public ICommand ViewLogsCommand { get; }
    public ICommand RefreshAppsCommand { get; }
    public ICommand ResetSettingsCommand { get; }
    public ICommand RefreshCommand => CheckBunCommand;

    #endregion

    #region 方法

    private async Task CheckBunAsync()
    {
        BunStatus = BunStatus.Checking;

        var detector = new BunDetector();
        var result = await detector.DetectAsync();

        if (result.IsFound)
        {
            BunStatus = BunStatus.Installed;
            BunVersion = result.Version ?? "未知";
            BunPath = result.Path;

            await _bunManager.InitializeAsync(result);
        }
        else
        {
            BunStatus = BunStatus.NotInstalled;
            BunVersion = "";
            BunPath = "";
        }
    }

    private void ShowInstallGuide()
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "https://bun.sh/docs/installation",
            UseShellExecute = true
        });
    }

    private async Task ToggleGatewayAsync()
    {
        if (IsGatewayRunning)
        {
            await _bunManager.StopAsync();
        }
        else
        {
            await _bunManager.StartAsync();
        }
    }

    private async Task RestartGatewayAsync()
    {
        await _bunManager.RestartAsync();
    }

    private void ViewLogs()
    {
        var logPath = System.IO.Path.Combine(
            _bunManager.DataDirectory,
            "elysia-gateway.log");

        if (System.IO.File.Exists(logPath))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = logPath,
                UseShellExecute = true
            });
        }
    }

    private void RefreshApps()
    {
        ConnectedApps.Clear();
    }

    private void ResetSettings()
    {
        IsAutoPort = true;
        ManualPortNumber = 34567;
        AutoRestart = true;
        SelectedLogLevel = "信息";
        DebugMode = false;
        SaveSettings();
    }

    private void LoadSettings()
    {
        var settings = _settingsService.GetSettings();
        IsAutoPort = settings.IsAutoPort;
        ManualPortNumber = settings.ManualPort;
        AutoRestart = settings.AutoRestart;
        SelectedLogLevel = MapLogLevelFromEnglish(settings.LogLevel);
        DebugMode = settings.DebugMode;
    }

    private void SaveSettings()
    {
        _settingsService.UpdateSettings(s =>
        {
            s.IsAutoPort = IsAutoPort;
            s.ManualPort = ManualPortNumber;
            s.AutoRestart = AutoRestart;
            s.LogLevel = MapLogLevelToEnglish(SelectedLogLevel);
            s.DebugMode = DebugMode;
        });
    }

    private static string MapLogLevelToEnglish(string chineseLevel) => chineseLevel switch
    {
        "调试" => "debug",
        "信息" => "info",
        "警告" => "warn",
        "错误" => "error",
        _ => "info"
    };

    private static string MapLogLevelFromEnglish(string englishLevel) => englishLevel?.ToLowerInvariant() switch
    {
        "debug" => "调试",
        "info" => "信息",
        "warn" => "警告",
        "error" => "错误",
        _ => "信息"
    };

    private void OnBunStatusChanged(object? sender, BunStatus status)
    {
        OnPropertyChanged(nameof(GatewayStatusDescription));
        OnPropertyChanged(nameof(GatewayStatusIcon));
        OnPropertyChanged(nameof(IsGatewayRunning));
        OnPropertyChanged(nameof(CanToggleGateway));
        OnPropertyChanged(nameof(ToggleGatewayButtonText));
    }

    private void OnBunManagerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BunProcessManager.GatewayPort))
        {
            OnPropertyChanged(nameof(GatewayPortText));
            OnPropertyChanged(nameof(GatewayStatusDescription));
        }

        if (e.PropertyName == nameof(BunProcessManager.StartTime))
        {
            OnPropertyChanged(nameof(GatewayUptimeText));
        }
    }

    #endregion

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public class ConnectedAppViewModel : INotifyPropertyChanged
{
    public string AppId { get; set; } = "";
    public string AppName { get; set; } = "";
    public string AppType { get; set; } = "";
    public string AppTypeIcon => AppType.ToLower() switch
    {
        "tauri" => "AppGeneric",
        "web" => "Globe",
        "rust" => "Code",
        "node" => "Code",
        _ => "PlugConnected"
    };
    public string AppDetails => $"{AppType} • {AppId}";
    public DateTime ConnectedAt { get; set; }
    public string ConnectedTime => ConnectedAt.ToString("HH:mm:ss");

    public event PropertyChangedEventHandler? PropertyChanged;
}

internal class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => _execute();
    public event EventHandler? CanExecuteChanged;
}

internal class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
    public async void Execute(object? parameter) => await _execute();
    public event EventHandler? CanExecuteChanged;
}
