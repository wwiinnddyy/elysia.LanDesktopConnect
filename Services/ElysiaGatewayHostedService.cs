using Microsoft.Extensions.Hosting;

namespace Elysia.LanDesktopConnect.Services;

public sealed class ElysiaGatewayHostedService : IHostedService
{
    private readonly ElysiaGatewayController _controller;

    public ElysiaGatewayHostedService(
        ElysiaGatewayController controller,
        IpcMessageRouter messageRouter)
    {
        _controller = controller;
        _ = messageRouter;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Detect the runtime during plugin startup, but leave gateway activation under user control.
        await _controller.DetectBunAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken)
        => _controller.StopAsync(cancellationToken);
}
