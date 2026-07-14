using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using LanMountainDesktop.PluginSdk;

namespace Elysia.LanDesktopConnect.Services;

public sealed partial class BunProcessManager : INotifyPropertyChanged, IDisposable
{
    private readonly ElysiaSettingsService _settingsService;
    private readonly IPluginMessageBus _messageBus;
    private readonly GatewayIpcServer _ipcServer;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly object _logLock = new();
    private readonly string _gatewayDirectory;

    private Process? _bunProcess;
    private CancellationTokenSource? _processCts;
    private int _restartCount;
    private bool _stopping;
    private bool _disposed;

    private BunStatus _status = BunStatus.NotStarted;
    private string? _bunPath;
    private string? _bunVersion;
    private int? _gatewayPort;
    private DateTimeOffset? _startTime;

    public BunProcessManager(
        IPluginRuntimeContext runtimeContext,
        ElysiaSettingsService settingsService,
        IPluginMessageBus messageBus,
        GatewayIpcServer ipcServer)
    {
        ArgumentNullException.ThrowIfNull(runtimeContext);

        _settingsService = settingsService;
        _messageBus = messageBus;
        _ipcServer = ipcServer;
        DataDirectory = runtimeContext.DataDirectory;
        _gatewayDirectory = Path.Combine(runtimeContext.PluginDirectory, "elysia-gateway");
        Directory.CreateDirectory(DataDirectory);
    }

    public BunStatus Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public string? BunPath
    {
        get => _bunPath;
        private set => SetField(ref _bunPath, value);
    }

    public string? BunVersion
    {
        get => _bunVersion;
        private set => SetField(ref _bunVersion, value);
    }

    public int? GatewayPort
    {
        get => _gatewayPort;
        private set => SetField(ref _gatewayPort, value);
    }

    public DateTimeOffset? StartTime
    {
        get => _startTime;
        private set => SetField(ref _startTime, value);
    }

    public string DataDirectory { get; }

    public bool IsTransportConnected => _ipcServer.IsConnected;

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler<BunStatus>? StatusChanged;

    public Task<bool> InitializeAsync(BunDetectionResult detectionResult)
    {
        ArgumentNullException.ThrowIfNull(detectionResult);

        if (!detectionResult.IsFound || string.IsNullOrWhiteSpace(detectionResult.Path))
        {
            BunPath = null;
            BunVersion = null;
            UpdateStatus(BunStatus.NotInstalled);
            return Task.FromResult(false);
        }

        BunPath = detectionResult.Path;
        BunVersion = detectionResult.Version;
        if (Status is BunStatus.NotStarted or BunStatus.NotInstalled or BunStatus.Checking)
        {
            UpdateStatus(BunStatus.Stopped);
        }

        return Task.FromResult(true);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Status is BunStatus.Running or BunStatus.Starting)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(BunPath))
            {
                throw new InvalidOperationException("Bun is not initialized. Detect Bun before starting the Elysia gateway.");
            }

            if (Status is BunStatus.Stopped or BunStatus.NotStarted or BunStatus.NotInstalled)
            {
                _restartCount = 0;
            }

            _stopping = false;
            _processCts?.Dispose();
            _processCts = new CancellationTokenSource();
            using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _processCts.Token);

            UpdateStatus(BunStatus.Starting);
            GatewayPort = null;

            try
            {
                EnsureGatewayFilesExist();
                await _ipcServer.StartAsync(startupCts.Token).ConfigureAwait(false);
                await EnsureGatewayDependenciesAsync(startupCts.Token).ConfigureAwait(false);

                var isAutoPort = _settingsService.GetValue("ipc.isAutoPort", true);
                var port = isAutoPort
                    ? 0
                    : _settingsService.GetValue("ipc.manualPort", 34567);

                var startInfo = new ProcessStartInfo
                {
                    FileName = BunPath,
                    Arguments = "run src/index.ts",
                    WorkingDirectory = _gatewayDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                startInfo.Environment["LMD_PLUGIN_PIPE"] = _ipcServer.Endpoint;
                startInfo.Environment["LMD_PLUGIN_DATA_DIR"] = DataDirectory;
                startInfo.Environment["LMD_GATEWAY_PORT"] = port.ToString();
                startInfo.Environment["LMD_GATEWAY_HOST"] = "127.0.0.1";
                startInfo.Environment["LMD_LOG_LEVEL"] = _settingsService.GetValue("ipc.logLevel", "info");

                var process = new Process
                {
                    StartInfo = startInfo,
                    EnableRaisingEvents = true
                };
                process.OutputDataReceived += OnProcessOutput;
                process.ErrorDataReceived += OnProcessError;
                process.Exited += OnProcessExited;

                if (!process.Start())
                {
                    process.Dispose();
                    throw new InvalidOperationException("Bun did not start the Elysia gateway process.");
                }

                _bunProcess = process;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                StartTime = DateTimeOffset.Now;

                await WaitForStartupAsync(process, startupCts.Token).ConfigureAwait(false);
                UpdateStatus(BunStatus.Running);
                LogInfo($"Elysia gateway started on 127.0.0.1:{GatewayPort}.");
            }
            catch (Exception ex)
            {
                LogError($"Failed to start Elysia gateway: {ex.Message}");
                await StopProcessCoreAsync(stopTransport: true).ConfigureAwait(false);
                UpdateStatus(BunStatus.Error);
                throw;
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _stopping = true;
            await StopProcessCoreAsync(stopTransport: true).ConfigureAwait(false);
            GatewayPort = null;
            StartTime = null;
            UpdateStatus(BunPath is null ? BunStatus.NotInstalled : BunStatus.Stopped);
        }
        finally
        {
            _stopping = false;
            _lifecycleLock.Release();
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken).ConfigureAwait(false);
        await Task.Delay(350, cancellationToken).ConfigureAwait(false);
        await StartAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureGatewayDependenciesAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = BunPath!,
            Arguments = "install --frozen-lockfile --production",
            WorkingDirectory = _gatewayDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(90));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch
        {
            TryKillProcess(process);
            throw;
        }

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Bun failed to restore Elysia gateway dependencies (exit {process.ExitCode}): {error.Trim()}");
        }

        if (_settingsService.GetValue("ipc.debugMode", false) && !string.IsNullOrWhiteSpace(output))
        {
            LogInfo($"Bun dependency restore: {output.Trim()}");
        }
    }

    private void EnsureGatewayFilesExist()
    {
        var requiredFiles = new[]
        {
            Path.Combine(_gatewayDirectory, "package.json"),
            Path.Combine(_gatewayDirectory, "bun.lock"),
            Path.Combine(_gatewayDirectory, "src", "index.ts")
        };

        var missingFile = requiredFiles.FirstOrDefault(path => !File.Exists(path));
        if (missingFile is not null)
        {
            throw new FileNotFoundException("The packaged Elysia gateway is incomplete.", missingFile);
        }
    }

    private async Task WaitForStartupAsync(Process process, CancellationToken cancellationToken)
    {
        var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < timeoutAt)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new InvalidOperationException($"Elysia gateway exited during startup with code {process.ExitCode}.");
            }

            if (GatewayPort.HasValue)
            {
                return;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException("Elysia gateway did not report a listening port within 20 seconds.");
    }

    private void OnProcessOutput(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data))
        {
            return;
        }

        LogInfo($"[Elysia] {e.Data}");
        var match = GatewayPortRegex().Match(e.Data);
        if (match.Success && int.TryParse(match.Groups["port"].Value, out var port))
        {
            GatewayPort = port;
        }
    }

    private void OnProcessError(object sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.Data))
        {
            LogError($"[Elysia] {e.Data}");
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (sender is Process process)
        {
            _ = HandleUnexpectedExitAsync(process);
        }
    }

    private async Task HandleUnexpectedExitAsync(Process process)
    {
        var shouldRestart = false;
        var restartDelay = TimeSpan.Zero;

        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_stopping || !ReferenceEquals(_bunProcess, process))
            {
                return;
            }

            var exitCode = TryGetExitCode(process);
            LogError($"Elysia gateway exited unexpectedly with code {exitCode}.");

            if (StartTime is { } startTime && DateTimeOffset.Now - startTime >= TimeSpan.FromMinutes(5))
            {
                _restartCount = 0;
            }

            await StopProcessCoreAsync(stopTransport: true).ConfigureAwait(false);
            GatewayPort = null;
            StartTime = null;
            UpdateStatus(BunStatus.Error);

            if (_settingsService.GetValue("ipc.autoRestart", true) && _restartCount < 5)
            {
                _restartCount++;
                restartDelay = TimeSpan.FromSeconds(Math.Min(_restartCount * 2, 30));
                shouldRestart = true;
                LogInfo($"Restarting Elysia gateway in {restartDelay.TotalSeconds:0} seconds (attempt {_restartCount}/5).");
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }

        if (!shouldRestart)
        {
            return;
        }

        try
        {
            await Task.Delay(restartDelay).ConfigureAwait(false);
            await StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogError($"Automatic gateway restart failed: {ex.Message}");
        }
    }

    private async Task StopProcessCoreAsync(bool stopTransport)
    {
        _processCts?.Cancel();

        var process = _bunProcess;
        _bunProcess = null;
        if (process is not null)
        {
            process.OutputDataReceived -= OnProcessOutput;
            process.ErrorDataReceived -= OnProcessError;
            process.Exited -= OnProcessExited;

            if (!process.HasExited)
            {
                TryKillProcess(process);
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            process.Dispose();
        }

        _processCts?.Dispose();
        _processCts = null;

        if (stopTransport)
        {
            await _ipcServer.StopAsync().ConfigureAwait(false);
        }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static int TryGetExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    private void UpdateStatus(BunStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
        _messageBus.Publish(new BunStatusChangedEvent(status));
    }

    private void LogInfo(string message) => AppendLog("INFO", message);

    private void LogError(string message) => AppendLog("ERROR", message);

    private void AppendLog(string level, string message)
    {
        try
        {
            var logFile = Path.Combine(DataDirectory, "elysia-gateway.log");
            var logEntry = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] [{level}] {message}{Environment.NewLine}";
            lock (_logLock)
            {
                File.AppendAllText(logFile, logEntry);
            }
        }
        catch
        {
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        StopAsync().GetAwaiter().GetResult();
        _lifecycleLock.Dispose();
        _disposed = true;
    }

    [GeneratedRegex(@"running\s+on\s+port\s+(?<port>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GatewayPortRegex();
}

public enum BunStatus
{
    NotStarted,
    Checking,
    Installed,
    NotInstalled,
    Stopped,
    Starting,
    Running,
    Error
}

public sealed record BunStatusChangedEvent(BunStatus Status);
