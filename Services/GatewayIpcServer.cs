using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LanMountainDesktop.PluginSdk;

namespace Elysia.LanDesktopConnect.Services;

public sealed class GatewayIpcServer : IDisposable, IAsyncDisposable
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    private readonly object _syncRoot = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly string _pipeName;
    private readonly string? _unixSocketPath;

    private CancellationTokenSource? _lifetimeCts;
    private Task? _acceptLoopTask;
    private NamedPipeServerStream? _pendingPipeServer;
    private Socket? _unixListener;
    private Stream? _connectionStream;
    private StreamWriter? _writer;
    private bool _disposed;

    public GatewayIpcServer(IPluginRuntimeContext runtimeContext)
    {
        ArgumentNullException.ThrowIfNull(runtimeContext);

        var identity = $"{runtimeContext.Manifest.Id}|{Environment.UserName}";
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..12];
        _pipeName = $"LMD_Elysia_{suffix}";

        if (OperatingSystem.IsWindows())
        {
            Endpoint = $"\\\\.\\pipe\\{_pipeName}";
        }
        else
        {
            _unixSocketPath = Path.Combine(Path.GetTempPath(), $"lmd-elysia-{suffix.ToLowerInvariant()}.sock");
            Endpoint = _unixSocketPath;
        }
    }

    public string Endpoint { get; }

    public bool IsRunning
    {
        get
        {
            lock (_syncRoot)
            {
                return _lifetimeCts is not null;
            }
        }
    }

    public bool IsConnected
    {
        get
        {
            lock (_syncRoot)
            {
                return _writer is not null;
            }
        }
    }

    public event EventHandler<IpcMessage>? MessageReceived;

    public event EventHandler? Connected;

    public event EventHandler? Disconnected;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_syncRoot)
        {
            if (_lifetimeCts is not null)
            {
                return Task.CompletedTask;
            }

            _lifetimeCts = new CancellationTokenSource();
            if (OperatingSystem.IsWindows())
            {
                _acceptLoopTask = RunWindowsAcceptLoopAsync(_lifetimeCts.Token);
            }
            else
            {
                PrepareUnixListener();
                _acceptLoopTask = RunUnixAcceptLoopAsync(_lifetimeCts.Token);
            }
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? lifetimeCts;
        Task? acceptLoopTask;
        NamedPipeServerStream? pendingPipeServer;
        Socket? unixListener;
        Stream? connectionStream;

        lock (_syncRoot)
        {
            lifetimeCts = _lifetimeCts;
            acceptLoopTask = _acceptLoopTask;
            pendingPipeServer = _pendingPipeServer;
            unixListener = _unixListener;
            connectionStream = _connectionStream;

            _lifetimeCts = null;
            _acceptLoopTask = null;
            _pendingPipeServer = null;
            _unixListener = null;
            _connectionStream = null;
            _writer = null;
        }

        if (lifetimeCts is null)
        {
            return;
        }

        lifetimeCts.Cancel();
        pendingPipeServer?.Dispose();
        unixListener?.Dispose();
        connectionStream?.Dispose();

        if (acceptLoopTask is not null)
        {
            try
            {
                await acceptLoopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        lifetimeCts.Dispose();
        DeleteUnixSocketFile();
    }

    public async Task SendAsync(IpcMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        StreamWriter writer;
        lock (_syncRoot)
        {
            writer = _writer ?? throw new InvalidOperationException("The Elysia gateway is not connected to the plugin IPC server.");
        }

        var json = JsonSerializer.Serialize(message, IpcJson.Options);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task RunWindowsAcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                lock (_syncRoot)
                {
                    _pendingPipeServer = server;
                }

                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                lock (_syncRoot)
                {
                    if (ReferenceEquals(_pendingPipeServer, server))
                    {
                        _pendingPipeServer = null;
                    }
                }

                await ProcessConnectionAsync(server, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            finally
            {
                lock (_syncRoot)
                {
                    if (ReferenceEquals(_pendingPipeServer, server))
                    {
                        _pendingPipeServer = null;
                    }
                }

                server?.Dispose();
            }
        }
    }

    private void PrepareUnixListener()
    {
        DeleteUnixSocketFile();

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(_unixSocketPath!));
        listener.Listen(1);
        _unixListener = listener;
    }

    private async Task RunUnixAcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket? client = null;
            try
            {
                Socket listener;
                lock (_syncRoot)
                {
                    listener = _unixListener ?? throw new ObjectDisposedException(nameof(GatewayIpcServer));
                }

                client = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                await using var stream = new NetworkStream(client, ownsSocket: true);
                client = null;
                await ProcessConnectionAsync(stream, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            finally
            {
                client?.Dispose();
            }
        }
    }

    private async Task ProcessConnectionAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        using var writer = new StreamWriter(stream, Utf8WithoutBom, leaveOpen: true)
        {
            AutoFlush = true
        };

        lock (_syncRoot)
        {
            _connectionStream = stream;
            _writer = writer;
        }

        RaiseEvent(Connected);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var message = JsonSerializer.Deserialize<IpcMessage>(line, IpcJson.Options);
                    if (message is not null)
                    {
                        RaiseMessageReceived(message);
                    }
                }
                catch (JsonException)
                {
                    // Ignore a malformed frame and keep the transport alive for subsequent frames.
                }
            }
        }
        catch (IOException)
        {
        }
        catch (SocketException)
        {
        }
        finally
        {
            var raiseDisconnected = false;
            lock (_syncRoot)
            {
                if (ReferenceEquals(_connectionStream, stream))
                {
                    _connectionStream = null;
                    _writer = null;
                    raiseDisconnected = true;
                }
            }

            if (raiseDisconnected)
            {
                RaiseEvent(Disconnected);
            }
        }
    }

    private void RaiseMessageReceived(IpcMessage message)
    {
        var handlers = MessageReceived?.GetInvocationList();
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.Cast<EventHandler<IpcMessage>>())
        {
            try
            {
                handler(this, message);
            }
            catch
            {
            }
        }
    }

    private void RaiseEvent(EventHandler? eventHandler)
    {
        var handlers = eventHandler?.GetInvocationList();
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.Cast<EventHandler>())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch
            {
            }
        }
    }

    private void DeleteUnixSocketFile()
    {
        if (!string.IsNullOrWhiteSpace(_unixSocketPath) && File.Exists(_unixSocketPath))
        {
            try
            {
                File.Delete(_unixSocketPath);
            }
            catch
            {
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        StopAsync().GetAwaiter().GetResult();
        _sendLock.Dispose();
        _disposed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _sendLock.Dispose();
        _disposed = true;
    }
}

public sealed record IpcMessage
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public long Timestamp { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public string Type { get; init; } = string.Empty;

    public string Sender { get; init; } = string.Empty;

    public string? Target { get; init; }

    public JsonElement? Payload { get; init; }

    public static IpcMessage Create(string type, string sender, object? payload = null, string? target = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(sender);

        return new IpcMessage
        {
            Type = type,
            Sender = sender,
            Target = target,
            Payload = payload is null
                ? null
                : JsonSerializer.SerializeToElement(payload, IpcJson.Options)
        };
    }
}

internal static class IpcJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
