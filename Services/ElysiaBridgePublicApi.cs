using System.Text.Json;
using dotnetCampus.Ipc.CompilerServices.Attributes;

namespace Elysia.LanDesktopConnect.Services;

[IpcPublic(IgnoresIpcException = true)]
public interface IElysiaBridgePublicApi
{
    ElysiaBridgeStatusSnapshot GetStatus();

    IReadOnlyList<ElysiaConnectedAppSnapshot> GetConnectedApps();

    Task<bool> StartGatewayAsync();

    Task StopGatewayAsync();

    Task<bool> RestartGatewayAsync();

    Task BroadcastJsonAsync(string messageType, string payloadJson);

    Task SendJsonAsync(string appId, string messageType, string payloadJson);
}

public sealed class ElysiaBridgePublicApi : IElysiaBridgePublicApi
{
    private readonly ElysiaGatewayController _controller;
    private readonly IpcMessageRouter _router;

    public ElysiaBridgePublicApi(
        ElysiaGatewayController controller,
        IpcMessageRouter router)
    {
        _controller = controller;
        _router = router;
    }

    public ElysiaBridgeStatusSnapshot GetStatus()
    {
        var manager = _controller.ProcessManager;
        return new ElysiaBridgeStatusSnapshot(
            manager.Status.ToString(),
            manager.BunPath is not null,
            manager.BunVersion,
            manager.GatewayPort,
            manager.IsTransportConnected,
            manager.StartTime,
            _router.GetConnectedApps().Count);
    }

    public IReadOnlyList<ElysiaConnectedAppSnapshot> GetConnectedApps()
    {
        return _router.GetConnectedApps()
            .Select(app => new ElysiaConnectedAppSnapshot(
                app.Id,
                app.Name,
                app.Type,
                app.Capabilities.ToArray(),
                app.ConnectedAt))
            .ToArray();
    }

    public Task<bool> StartGatewayAsync() => _controller.StartAsync();

    public Task StopGatewayAsync() => _controller.StopAsync();

    public Task<bool> RestartGatewayAsync() => _controller.RestartAsync();

    public Task BroadcastJsonAsync(string messageType, string payloadJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        var payload = ParsePayload(payloadJson);
        return _router.SendToGatewayAsync(
            IpcMessage.Create(
                "host:broadcast",
                "lanmountain-desktop.public-ipc",
                new { type = messageType.Trim(), payload }));
    }

    public Task SendJsonAsync(string appId, string messageType, string payloadJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        var payload = ParsePayload(payloadJson);
        return _router.SendToGatewayAsync(
            IpcMessage.Create(
                "host:send",
                "lanmountain-desktop.public-ipc",
                new { type = messageType.Trim(), payload },
                appId.Trim()));
    }

    private static JsonElement ParsePayload(string payloadJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);
        using var document = JsonDocument.Parse(payloadJson);
        return document.RootElement.Clone();
    }
}

public sealed record ElysiaBridgeStatusSnapshot(
    string Status,
    bool IsBunAvailable,
    string? BunVersion,
    int? GatewayPort,
    bool IsTransportConnected,
    DateTimeOffset? StartedAt,
    int ConnectedAppCount);

public sealed record ElysiaConnectedAppSnapshot(
    string Id,
    string Name,
    string Type,
    IReadOnlyList<string> Capabilities,
    DateTimeOffset ConnectedAt);
