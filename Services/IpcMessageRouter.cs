using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LanMountainDesktop.PluginSdk;

namespace Elysia.LanDesktopConnect.Services;

public class IpcMessageRouter
{
    private readonly NamedPipeClient _pipeClient;
    private readonly IPluginMessageBus _messageBus;
    private readonly Dictionary<string, Func<IpcMessage, Task>> _handlers = new();

    public IpcMessageRouter(
        NamedPipeClient pipeClient,
        IPluginMessageBus messageBus)
    {
        _pipeClient = pipeClient;
        _messageBus = messageBus;

        // 订阅管道消息
        _pipeClient.MessageReceived += OnPipeMessageReceived;
    }

    private void OnPipeMessageReceived(object? sender, IpcMessage message)
    {
        // 将消息发布到插件内部消息总线
        _messageBus.Publish(new ExternalMessageReceivedEvent(message));

        // 调用注册的处理程序
        if (_handlers.TryGetValue(message.Type, out var handler))
        {
            _ = handler(message);
        }
    }

    public void RegisterHandler(string messageType, Func<IpcMessage, Task> handler)
    {
        _handlers[messageType] = handler;
    }

    public async Task SendToElysiaAsync(IpcMessage message)
    {
        if (!_pipeClient.IsConnected)
        {
            throw new InvalidOperationException("Not connected to Elysia gateway");
        }

        await _pipeClient.SendAsync(message);
    }

    public async Task BroadcastToExternalAppsAsync(object data)
    {
        await SendToElysiaAsync(new IpcMessage
        {
            Type = "broadcast",
            Sender = "lanmountain-desktop",
            Payload = data
        });
    }
}

public record ExternalMessageReceivedEvent(IpcMessage Message);
