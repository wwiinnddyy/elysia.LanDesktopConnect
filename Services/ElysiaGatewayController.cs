namespace Elysia.LanDesktopConnect.Services;

public sealed class ElysiaGatewayController
{
    private readonly BunDetector _bunDetector;
    private readonly BunProcessManager _bunManager;
    private readonly SemaphoreSlim _detectionLock = new(1, 1);

    public ElysiaGatewayController(BunDetector bunDetector, BunProcessManager bunManager)
    {
        _bunDetector = bunDetector;
        _bunManager = bunManager;
    }

    public BunProcessManager ProcessManager => _bunManager;

    public async Task<BunDetectionResult> DetectBunAsync(CancellationToken cancellationToken = default)
    {
        await _detectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await _bunDetector.DetectAsync(cancellationToken).ConfigureAwait(false);
            await _bunManager.InitializeAsync(result).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _detectionLock.Release();
        }
    }

    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_bunManager.BunPath))
        {
            var detection = await DetectBunAsync(cancellationToken).ConfigureAwait(false);
            if (!detection.IsFound)
            {
                return false;
            }
        }

        await _bunManager.StartAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
        => _bunManager.StopAsync(cancellationToken);

    public async Task<bool> RestartAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_bunManager.BunPath))
        {
            return await StartAsync(cancellationToken).ConfigureAwait(false);
        }

        await _bunManager.RestartAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
