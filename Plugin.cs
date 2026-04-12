using Elysia.LanDesktopConnect.Services;
using Elysia.LanDesktopConnect.Settings;
using LanMountainDesktop.PluginSdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Elysia.LanDesktopConnect;

[PluginEntrance]
public sealed class Plugin : PluginBase
{
    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        // 注册 Bun 检测服务
        services.AddSingleton<BunDetector>();
        
        // 注册 Bun 进程管理器
        services.AddSingleton<BunProcessManager>();
        
        // 注册托管服务（启动 Bun 进程）
        services.AddHostedService<ElysiaGatewayHostedService>();
        
        // 注册 Named Pipe 客户端（与 Bun 通信）
        services.AddSingleton<NamedPipeClient>();
        
        // 注册消息路由器
        services.AddSingleton<IpcMessageRouter>();
        
        // 注册设置页面 ViewModel
        services.AddSingleton<IpcBridgeSettingsViewModel>();
        
        // 注册设置页面
        services.AddPluginSettingsSection(
            id: "ipc-bridge",
            titleLocalizationKey: "settings.ipc_bridge.title",
            configure: builder =>
            {
                // 使用自定义设置页面
            },
            descriptionLocalizationKey: "settings.ipc_bridge.description",
            iconKey: "PlugConnected",
            sortOrder: 100);
    }
}
