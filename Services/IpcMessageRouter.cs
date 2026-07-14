using System.Collections.Concurrent;
using System.Text.Json;
using LanMountainDesktop.PluginSdk;

namespace Elysia.LanDesktopConnect.Services;

public sealed class IpcMessageRouter : IDisposable
{
    private readonly GatewayIpcServer _ipcServer;
    private readonly IPluginMessageBus _messageBus;
    private readonly ConcurrentDictionary<string, Func<IpcMessage, Task>> _handlers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConnectedAppInfo> _connectedApps =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public IpcMessageRouter(GatewayIpcServer ipcServer, IPluginMessageBus messageBus)
    {
        _ipcServer = ipcServer;
        _messageBus = messageBus;

        _ipcServer.MessageReceived += OnGatewayMessageReceived;
        _ipcServer.Connected += OnGatewayConnected;
        _ipcServer.Disconnected += OnGatewayDisconnected;
    }

    public IReadOnlyList<ConnectedAppInfo> GetConnectedApps()
    {
        return _connectedApps.Values
            .OrderBy(app => app.ConnectedAt)
            .ThenBy(app => app.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IDisposable RegisterHandler(string messageType, Func<IpcMessage, Task> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        ArgumentNullException.ThrowIfNull(handler);

        _handlers[messageType.Trim()] = handler;
        return new HandlerRegistration(_handlers, messageType.Trim(), handler);
    }

    public Task SendToGatewayAsync(IpcMessage message, CancellationToken cancellationToken = default)
    {
        return _ipcServer.SendAsync(message, cancellationToken);
    }

    public Task BroadcastToExternalAppsAsync(object? payload, CancellationToken cancellationToken = default)
    {
        return SendToGatewayAsync(
            IpcMessage.Create("host:broadcast", "lanmountain-desktop", payload),
            cancellationToken);
    }

    public Task SendToExternalAppAsync(
        string appId,
        object? payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        return SendToGatewayAsync(
            IpcMessage.Create("host:send", "lanmountain-desktop", payload, appId.Trim()),
            cancellationToken);
    }

    public Task RegisterHostCapabilityAsync(
        string id,
        string name,
        string description,
        CancellationToken cancellationToken = default)
    {
        return SendToGatewayAsync(
            IpcMessage.Create(
                "host:capability:register",
                "lanmountain-desktop",
                new { id, name, description }),
            cancellationToken);
    }

    private void OnGatewayConnected(object? sender, EventArgs e)
    {
        _messageBus.Publish(new GatewayTransportConnectionChangedEvent(true));
    }

    private void OnGatewayDisconnected(object? sender, EventArgs e)
    {
        _connectedApps.Clear();
        PublishConnectedAppsChanged();
        _messageBus.Publish(new GatewayTransportConnectionChangedEvent(false));
    }

    private void OnGatewayMessageReceived(object? sender, IpcMessage message)
    {
        UpdateConnectedApps(message);
        _messageBus.Publish(new ExternalMessageReceivedEvent(message));

        if (_handlers.TryGetValue(message.Type, out var handler))
        {
            _ = InvokeHandlerAsync(handler, message);
        }
    }

    private async Task InvokeHandlerAsync(Func<IpcMessage, Task> handler, IpcMessage message)
    {
        try
        {
            await handler(message).ConfigureAwait(false);
        }
        catch
        {
            // A plugin-internal handler must not terminate the gateway receive loop.
        }
    }

    private void UpdateConnectedApps(IpcMessage message)
    {
        if (string.Equals(message.Type, "register", StringComparison.OrdinalIgnoreCase))
        {
            var app = ParseConnectedApp(message);
            _connectedApps[app.Id] = app;
            PublishConnectedAppsChanged();
            return;
        }

        if (string.Equals(message.Type, "unregister", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(message.Type, "app:disconnected", StringComparison.OrdinalIgnoreCase))
        {
            var appId = message.Sender;
            if (string.IsNullOrWhiteSpace(appId) && message.Payload is { } payload)
            {
                appId = GetString(payload, "id") ?? GetString(payload, "appId") ?? string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(appId) && _connectedApps.TryRemove(appId, out _))
            {
                PublishConnectedAppsChanged();
            }
        }
    }

    private static ConnectedAppInfo ParseConnectedApp(IpcMessage message)
    {
        var payload = message.Payload;
        var id = payload is { } payloadValue
            ? GetString(payloadValue, "id") ?? message.Sender
            : message.Sender;
        if (string.IsNullOrWhiteSpace(id))
        {
            id = $"app-{message.Id}";
        }

        var name = payload is { } namePayload
            ? GetString(namePayload, "name") ?? id
            : id;
        var type = payload is { } typePayload
            ? GetString(typePayload, "type") ?? "unknown"
            : "unknown";
        var capabilities = payload is { } capabilitiesPayload
            ? GetStringArray(capabilitiesPayload, "capabilities")
            : [];

        return new ConnectedAppInfo(
            id,
            name,
            type,
            capabilities,
            DateTimeOffset.FromUnixTimeMilliseconds(message.Timestamp));
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static IReadOnlyList<string> GetStringArray(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return property.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void PublishConnectedAppsChanged()
    {
        _messageBus.Publish(new ConnectedAppsChangedEvent(GetConnectedApps()));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _ipcServer.MessageReceived -= OnGatewayMessageReceived;
        _ipcServer.Connected -= OnGatewayConnected;
        _ipcServer.Disconnected -= OnGatewayDisconnected;
        _disposed = true;
    }

    private sealed class HandlerRegistration(
        ConcurrentDictionary<string, Func<IpcMessage, Task>> handlers,
        string messageType,
        Func<IpcMessage, Task> handler) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            handlers.TryRemove(new KeyValuePair<string, Func<IpcMessage, Task>>(messageType, handler));
            _disposed = true;
        }
    }
}

public sealed record ConnectedAppInfo(
    string Id,
    string Name,
    string Type,
    IReadOnlyList<string> Capabilities,
    DateTimeOffset ConnectedAt);

public sealed record ExternalMessageReceivedEvent(IpcMessage Message);

public sealed record ConnectedAppsChangedEvent(IReadOnlyList<ConnectedAppInfo> Apps);

public sealed record GatewayTransportConnectionChangedEvent(bool IsConnected);
