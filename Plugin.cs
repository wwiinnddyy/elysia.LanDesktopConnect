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
        services.AddSingleton(provider =>
        {
            var runtimeContext = provider.GetRequiredService<IPluginRuntimeContext>();
            Directory.CreateDirectory(runtimeContext.DataDirectory);
            return new ElysiaSettingsService(runtimeContext.DataDirectory);
        });

        services.AddSingleton<BunDetector>();
        
        services.AddSingleton<BunProcessManager>();
        
        services.AddHostedService<ElysiaGatewayHostedService>();
        
        services.AddSingleton<NamedPipeClient>();
        
        services.AddSingleton<IpcMessageRouter>();
        
        services.AddSingleton<IpcBridgeSettingsViewModel>();
        
        services.AddPluginSettingsSection<IpcBridgeSettingsPage>(
            id: "ipc-bridge",
            titleLocalizationKey: "settings.ipc_bridge.title",
            descriptionLocalizationKey: "settings.ipc_bridge.description",
            iconKey: "PlugConnected",
            sortOrder: 100);
    }
}
