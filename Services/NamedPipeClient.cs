using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Elysia.LanDesktopConnect.Services;

public class NamedPipeClient : IDisposable
{
    private NamedPipeClientStream? _pipeClient;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private readonly CancellationTokenSource _cts = new();
    private bool _isConnected;

    public event EventHandler<IpcMessage>? MessageReceived;
    public event EventHandler? Connected;
    public event EventHandler? Disconnected;

    public bool IsConnected => _isConnected;

    public async Task ConnectAsync(string pipeName)
    {
        try
        {
            _pipeClient = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            await _pipeClient.ConnectAsync(5000, _cts.Token);
            
            _reader = new StreamReader(_pipeClient, Encoding.UTF8);
            _writer = new StreamWriter(_pipeClient, Encoding.UTF8) { AutoFlush = true };
            
            _isConnected = true;
            Connected?.Invoke(this, EventArgs.Empty);

            // 启动接收循环
            _ = ReceiveLoopAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NamedPipeClient] Connection failed: {ex.Message}");
            throw;
        }
    }

    public async Task SendAsync(IpcMessage message)
    {
        if (!_isConnected || _writer == null)
        {
            throw new InvalidOperationException("Not connected to pipe");
        }

        var json = JsonSerializer.Serialize(message);
        await _writer.WriteLineAsync(json);
    }

    private async Task ReceiveLoopAsync()
    {
        if (_reader == null) return;

        try
        {
            while (!_cts.IsCancellationRequested && _pipeClient?.IsConnected == true)
            {
                var line = await _reader.ReadLineAsync();
                if (line == null) break;

                try
                {
                    var message = JsonSerializer.Deserialize<IpcMessage>(line);
                    if (message != null)
                    {
                        MessageReceived?.Invoke(this, message);
                    }
                }
                catch (JsonException ex)
                {
                    Console.WriteLine($"[NamedPipeClient] Failed to deserialize message: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NamedPipeClient] Receive error: {ex.Message}");
        }
        finally
        {
            _isConnected = false;
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _reader?.Dispose();
        _writer?.Dispose();
        _pipeClient?.Dispose();
        _cts.Dispose();
    }
}

public record IpcMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public string Type { get; set; } = "";
    public string Sender { get; set; } = "";
    public string? Target { get; set; }
    public object? Payload { get; set; }
}
