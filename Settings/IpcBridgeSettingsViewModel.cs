using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Elysia.LanDesktopConnect.Services;
using LanMountainDesktop.PluginSdk;
using ReactiveUI;

namespace Elysia.LanDesktopConnect.Settings;

public class IpcBridgeSettingsViewModel : ReactiveObject
{
    private readonly IPluginSettingsService _settingsService;
    private readonly BunProcessManager _bunManager;
    private readonly IPluginMessageBus _messageBus;

    public IpcBridgeSettingsViewModel(
        IPluginSettingsService settingsService,
        BunProcessManager bunManager,
        IPluginMessageBus messageBus)
    {
        _settingsService = settingsService;
        _bunManager = bunManager;
        _messageBus = messageBus;

        // 初始化命令
        CheckBunCommand = ReactiveCommand.CreateFromTask(CheckBunAsync);
        InstallBunCommand = ReactiveCommand.Create(ShowInstallGuide);
        ToggleGatewayCommand = ReactiveCommand.CreateFromTask(ToggleGatewayAsync);
        RestartGatewayCommand = ReactiveCommand.CreateFromTask(RestartGatewayAsync);
        ViewLogsCommand = ReactiveCommand.Create(ViewLogs);
        RefreshAppsCommand = ReactiveCommand.Create(RefreshApps);
        ResetSettingsCommand = ReactiveCommand.Create(ResetSettings);

        // 订阅状态变化
        _bunManager.WhenAnyValue(x => x.Status)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(GatewayStatusDescription));
                this.RaisePropertyChanged(nameof(GatewayStatusIcon));
                this.RaisePropertyChanged(nameof(IsGatewayRunning));
                this.RaisePropertyChanged(nameof(CanToggleGateway));
                this.RaisePropertyChanged(nameof(ToggleGatewayButtonText));
            });

        _bunManager.WhenAnyValue(x => x.GatewayPort)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(GatewayPortText)));

        // 定时更新
        Observable.Interval(TimeSpan.FromSeconds(1))
            .Subscribe(_ => this.RaisePropertyChanged(nameof(GatewayUptimeText)));

        // 加载设置
        LoadSettings();

        // 初始检测
        _ = CheckBunAsync();
    }

    #region Bun 状态

    private BunStatus _bunStatus = BunStatus.Checking;
    private string _bunVersion = "";
    private string _bunPath = "";

    public BunStatus BunStatus
    {
        get => _bunStatus;
        set => this.RaiseAndSetIfChanged(ref _bunStatus, value);
    }

    public string BunVersion
    {
        get => _bunVersion;
        set => this.RaiseAndSetIfChanged(ref _bunVersion, value);
    }

    public string BunPath
    {
        get => _bunPath;
        set => this.RaiseAndSetIfChanged(ref _bunPath, value);
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

    public string BunStatusIcon => BunStatus == BunStatus.Installed
        ? "CheckmarkCircle"
        : "ErrorCircle";

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
        set { this.RaiseAndSetIfChanged(ref _isAutoPort, value); SaveSettings(); }
    }

    public int ManualPortNumber
    {
        get => _manualPort;
        set { this.RaiseAndSetIfChanged(ref _manualPort, value); SaveSettings(); }
    }

    public bool AutoRestart
    {
        get => _autoRestart;
        set { this.RaiseAndSetIfChanged(ref _autoRestart, value); SaveSettings(); }
    }

    public string[] LogLevels { get; } = { "调试", "信息", "警告", "错误" };

    public string SelectedLogLevel
    {
        get => _selectedLogLevel;
        set { this.RaiseAndSetIfChanged(ref _selectedLogLevel, value); SaveSettings(); }
    }

    public bool DebugMode
    {
        get => _debugMode;
        set { this.RaiseAndSetIfChanged(ref _debugMode, value); SaveSettings(); }
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

        if (result.Found)
        {
            BunStatus = BunStatus.Installed;
            BunVersion = result.Version ?? "未知";
            BunPath = result.Path;

            // 初始化 Bun 进程管理器
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
        // 显示安装指南
        var guide = @"安装 Bun：

Windows:
powershell -c ""irm bun.sh/install.ps1 | iex""

Linux/macOS:
curl -fsSL https://bun.sh/install | bash

安装完成后点击""重新检测""。
";

        // 这里可以通过消息总线或对话框显示
        // 简化处理，直接打开浏览器
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
        // 从 Elysia 获取连接的应用列表
        ConnectedApps.Clear();
        // TODO: 实现获取逻辑
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
        IsAutoPort = _settingsService.GetValue("ipc.isAutoPort", true);
        ManualPortNumber = _settingsService.GetValue("ipc.manualPort", 34567);
        AutoRestart = _settingsService.GetValue("ipc.autoRestart", true);
        SelectedLogLevel = _settingsService.GetValue("ipc.logLevel", "信息");
        DebugMode = _settingsService.GetValue("ipc.debugMode", false);
    }

    private void SaveSettings()
    {
        _settingsService.SetValue("ipc.isAutoPort", IsAutoPort);
        _settingsService.SetValue("ipc.manualPort", ManualPortNumber);
        _settingsService.SetValue("ipc.autoRestart", AutoRestart);
        _settingsService.SetValue("ipc.logLevel", SelectedLogLevel);
        _settingsService.SetValue("ipc.debugMode", DebugMode);
    }

    #endregion
}

public class ConnectedAppViewModel : ReactiveObject
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
}
