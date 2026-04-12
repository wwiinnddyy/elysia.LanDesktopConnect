using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using LanMountainDesktop.PluginSdk;

namespace Elysia.LanDesktopConnect.Services;

public class BunProcessManager : INotifyPropertyChanged, IDisposable
{
    private readonly IPluginSettingsService _settingsService;
    private readonly IPluginMessageBus _messageBus;
    private Process? _bunProcess;
    private CancellationTokenSource? _cts;
    private int _restartCount;
    private readonly object _lock = new();

    private BunStatus _status = BunStatus.NotStarted;
    private string? _bunPath;
    private string? _bunVersion;
    private int? _gatewayPort;
    private DateTime? _startTime;

    public BunStatus Status
    {
        get => _status;
        private set { _status = value; OnPropertyChanged(); }
    }

    public string? BunPath
    {
        get => _bunPath;
        private set { _bunPath = value; OnPropertyChanged(); }
    }

    public string? BunVersion
    {
        get => _bunVersion;
        private set { _bunVersion = value; OnPropertyChanged(); }
    }

    public int? GatewayPort
    {
        get => _gatewayPort;
        private set { _gatewayPort = value; OnPropertyChanged(); }
    }

    public DateTime? StartTime
    {
        get => _startTime;
        private set { _startTime = value; OnPropertyChanged(); }
    }

    public string DataDirectory { get; }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<BunStatus>? StatusChanged;

    public BunProcessManager(IPluginRuntimeContext runtimeContext, IPluginSettingsService settingsService, IPluginMessageBus messageBus)
    {
        _settingsService = settingsService;
        _messageBus = messageBus;
        DataDirectory = runtimeContext.DataDirectory;
        Directory.CreateDirectory(DataDirectory);
    }

    public async Task<bool> InitializeAsync(BunDetectionResult detectionResult)
    {
        if (!detectionResult.IsFound || string.IsNullOrEmpty(detectionResult.Path))
        {
            UpdateStatus(BunStatus.BunNotInstalled);
            return false;
        }

        BunPath = detectionResult.Path;
        BunVersion = detectionResult.Version;
        UpdateStatus(BunStatus.Stopped);
        return true;
    }

    public async Task StartAsync()
    {
        if (string.IsNullOrEmpty(BunPath))
        {
            throw new InvalidOperationException("Bun path is not set. Call InitializeAsync first.");
        }

        lock (_lock)
        {
            if (Status == BunStatus.Running || Status == BunStatus.Starting)
            {
                return;
            }
        }

        _cts = new CancellationTokenSource();
        UpdateStatus(BunStatus.Starting);

        try
        {
            var isAutoPort = _settingsService.GetValue("ipc.isAutoPort", true);
            var port = isAutoPort ? 0 : _settingsService.GetValue("ipc.manualPort", 34567);

            var startInfo = new ProcessStartInfo
            {
                FileName = BunPath,
                Arguments = "run src/index.ts",
                WorkingDirectory = GetElysiaGatewayDirectory(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            startInfo.Environment["LMD_PLUGIN_PIPE"] = GetPipeName();
            startInfo.Environment["LMD_PLUGIN_DATA_DIR"] = DataDirectory;
            startInfo.Environment["LMD_GATEWAY_PORT"] = port.ToString();
            startInfo.Environment["LMD_LOG_LEVEL"] = _settingsService.GetValue("ipc.logLevel", "info");

            _bunProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            _bunProcess.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    LogInfo($"[Elysia] {e.Data}");
                    ParsePortFromOutput(e.Data);
                }
            };

            _bunProcess.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    LogError($"[Elysia Error] {e.Data}");
                }
            };

            _bunProcess.Exited += async (s, e) =>
            {
                LogInfo($"Elysia process exited with code {_bunProcess.ExitCode}");

                if (_cts?.IsCancellationRequested == false)
                {
                    await HandleProcessExitAsync();
                }
            };

            _bunProcess.Start();
            _bunProcess.BeginOutputReadLine();
            _bunProcess.BeginErrorReadLine();

            StartTime = DateTime.Now;

            await WaitForStartupAsync(_cts.Token);

            UpdateStatus(BunStatus.Running);
            _restartCount = 0;

            LogInfo($"Elysia gateway started on port {GatewayPort}");
        }
        catch (Exception ex)
        {
            LogError($"Failed to start Elysia: {ex.Message}");
            UpdateStatus(BunStatus.Error);
            throw;
        }
    }

    public async Task StopAsync()
    {
        lock (_lock)
        {
            if (Status != BunStatus.Running && Status != BunStatus.Starting)
            {
                return;
            }
        }

        _cts?.Cancel();

        if (_bunProcess != null && !_bunProcess.HasExited)
        {
            try
            {
                _bunProcess.Kill(true);
                await _bunProcess.WaitForExitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                LogError($"Error stopping Elysia: {ex.Message}");
            }
        }

        UpdateStatus(BunStatus.Stopped);
        GatewayPort = null;
        StartTime = null;
    }

    public async Task RestartAsync()
    {
        await StopAsync();
        await Task.Delay(500);
        await StartAsync();
    }

    private async Task HandleProcessExitAsync()
    {
        UpdateStatus(BunStatus.Error);
        GatewayPort = null;

        var autoRestart = _settingsService.GetValue("ipc.autoRestart", true);

        if (autoRestart && _restartCount < 5 && _cts?.IsCancellationRequested == false)
        {
            _restartCount++;
            var delay = TimeSpan.FromSeconds(Math.Min(_restartCount * 2, 30));

            LogInfo($"Auto-restarting in {delay.TotalSeconds} seconds... (attempt {_restartCount}/5)");

            await Task.Delay(delay);

            try
            {
                await StartAsync();
            }
            catch (Exception ex)
            {
                LogError($"Auto-restart failed: {ex.Message}");
            }
        }
    }

    private async Task WaitForStartupAsync(CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(10);
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed < timeout)
        {
            if (GatewayPort.HasValue)
            {
                return;
            }

            await Task.Delay(100, ct);
        }

        throw new TimeoutException("Elysia gateway failed to start within 10 seconds");
    }

    private void ParsePortFromOutput(string line)
    {
        if (line.Contains("port") || line.Contains("Port"))
        {
            var parts = line.Split(' ');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (int.TryParse(parts[i + 1], out var port) && port > 0)
                {
                    GatewayPort = port;
                    break;
                }
            }
        }
    }

    private string GetElysiaGatewayDirectory()
    {
        var pluginDir = Path.GetDirectoryName(GetType().Assembly.Location)!;
        return Path.Combine(pluginDir, "elysia-gateway");
    }

    private string GetPipeName()
    {
        var userName = Environment.UserName;
        var pipeId = $"LMD_Elysia_{userName}".GetHashCode().ToString("X8");

        if (OperatingSystem.IsWindows())
        {
            return $"\\\\.\\pipe\\{pipeId}";
        }
        else
        {
            return Path.Combine(Path.GetTempPath(), $"lmd-elysia-{pipeId}.sock");
        }
    }

    private void UpdateStatus(BunStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
        _messageBus.Publish(new BunStatusChangedEvent(status));
    }

    private void LogInfo(string message)
    {
        var logFile = Path.Combine(DataDirectory, "elysia-gateway.log");
        var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [INFO] {message}{Environment.NewLine}";
        File.AppendAllText(logFile, logEntry);
    }

    private void LogError(string message)
    {
        var logFile = Path.Combine(DataDirectory, "elysia-gateway.log");
        var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [ERROR] {message}{Environment.NewLine}";
        File.AppendAllText(logFile, logEntry);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void Dispose()
    {
        _ = StopAsync();
        _bunProcess?.Dispose();
        _cts?.Dispose();
    }
}

public enum BunStatus
{
    NotStarted,
    BunNotInstalled,
    Stopped,
    Starting,
    Running,
    Error
}

public record BunStatusChangedEvent(BunStatus Status);
